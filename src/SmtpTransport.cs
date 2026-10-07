using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PomoCC
{
    /// <summary>
    /// 自带 SMTP 客户端：同时支持 465（隐式 SSL）与 587/25（STARTTLS）。
    /// .NET 自带的 SmtpClient 不支持 465，国内邮箱大量使用 465，所以这里自己实现。
    /// </summary>
    public static class SmtpTransport
    {
        /// <summary>建立 TCP 连接的显式超时。ReceiveTimeout 只管读，管不了连接阶段。</summary>
        public const int ConnectTimeoutMs = 15000;

        /// <summary>带超时的连接：超时就主动断开并报错，不让界面卡在系统默认超时上。</summary>
        private static void Connect(TcpClient client, string host, int port, int timeoutMs)
        {
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            try
            {
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    try { client.Close(); } catch { }
                    throw new IOException(string.Format("连接 {0}:{1} 超时（{2} 秒没连上）", host, port, timeoutMs / 1000));
                }
                try
                {
                    client.EndConnect(ar);      // 连接被拒等错误在这里抛出
                }
                catch (Exception ex)
                {
                    // 统一转成可读错误（含目标地址），不要把 SocketException 直接抛给用户
                    throw new IOException(string.Format("连接 {0}:{1} 失败：{2}", host, port, ex.Message));
                }
            }
            finally
            {
                // 连接结束（无论成败）都要释放句柄，别把等待句柄泄漏出去
                try { ar.AsyncWaitHandle.Close(); } catch { }
            }
        }

        /// <summary>协议边界校验：不能只依赖设置界面，直接调用发送入口也必须拦住。</summary>
        public static void ValidateAddresses(string host, string from, string to)
        {
            string why;
            if (string.IsNullOrEmpty(host)) throw new InvalidOperationException("没有填写 SMTP 服务器地址。");
            if (SettingsValidator.HasControlChars(host) || host.IndexOf(' ') >= 0)
                throw new InvalidOperationException("SMTP 服务器地址里不能有空格或换行。");
            if (!SettingsValidator.IsValidEmail(from, out why))
                throw new InvalidOperationException("发件邮箱不合法：" + why + "。");
            if (!SettingsValidator.IsValidEmail(to, out why))
                throw new InvalidOperationException("收件邮箱不合法：" + why + "。");
        }

        public static void Send(string host, int port, bool startTls, string user, string pass,
                                string from, string to, string subject, string body)
        {
            ValidateAddresses(host, from, to);       // 发信前重新校验（防 CRLF 注入）
            using (TcpClient client = new TcpClient())
            {
                Connect(client, host, port, ConnectTimeoutMs);
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;

                Stream stream = client.GetStream();
                bool implicitSsl = (port == 465 || port == 994);
                if (implicitSsl) stream = WrapSsl(stream, host);

                SmtpSession s = new SmtpSession(stream);
                s.ReadResponse(220, "服务器问候");

                string caps = s.Command("EHLO " + LocalHostName(), 250);

                if (!implicitSsl && startTls)
                {
                    if (caps.IndexOf("STARTTLS", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException(
                            "服务器不支持 STARTTLS。如果用的是 465 端口，请把端口改成 465（本程序会自动使用 SSL）。");
                    s.Command("STARTTLS", 220);
                    stream = WrapSsl(s.Stream, host);
                    s = new SmtpSession(stream);
                    s.Command("EHLO " + LocalHostName(), 250);
                }

                bool needAuth = !string.IsNullOrEmpty(user);
                if (needAuth)
                {
                    s.Command("AUTH LOGIN", 334);
                    s.Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(user)), 334);
                    s.Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(pass)), 235);
                }

                s.Command("MAIL FROM:<" + from + ">", 250);
                s.Command("RCPT TO:<" + to + ">", 250);
                s.Command("DATA", 354);
                // 报文末尾必须以单独一行 "." 结束 DATA 段
                s.Write(BuildMessage(from, to, subject, body) + ".\r\n");
                s.ReadResponse(250, "邮件接收结果");
                try { s.Command("QUIT", 221); } catch { }
            }
        }

        /// <summary>只做连接/加密握手探测，不登录也不发信（供“测试连接”使用）。</summary>
        public static string Probe(string host, int port, bool startTls)
        {
            if (string.IsNullOrEmpty(host)) throw new InvalidOperationException("没有填写 SMTP 服务器地址。");
            if (SettingsValidator.HasControlChars(host) || host.IndexOf(' ') >= 0)
                throw new InvalidOperationException("SMTP 服务器地址里不能有空格或换行。");
            using (TcpClient client = new TcpClient())
            {
                Connect(client, host, port, ConnectTimeoutMs);
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;

                Stream stream = client.GetStream();
                bool implicitSsl = (port == 465 || port == 994);
                if (implicitSsl) stream = WrapSsl(stream, host);

                SmtpSession s = new SmtpSession(stream);
                string greeting = s.ReadResponse(220, "服务器问候");
                string caps = s.Command("EHLO " + LocalHostName(), 250);

                string note;
                if (implicitSsl)
                {
                    note = "SSL 握手成功（端口 " + port + " 用隐式 SSL）";
                }
                else if (caps.IndexOf("STARTTLS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!startTls)
                    {
                        note = "服务器支持 STARTTLS，但你把「使用 STARTTLS」取消勾选了";
                    }
                    else
                    {
                        s.Command("STARTTLS", 220);
                        stream = WrapSsl(s.Stream, host);
                        s = new SmtpSession(stream);
                        s.Command("EHLO " + LocalHostName(), 250);
                        note = "STARTTLS 升级成功，通道已加密";
                    }
                }
                else
                {
                    note = "服务器没声明 STARTTLS（明文连接，一般不可用于登录）";
                }

                try { s.Command("QUIT", 221); } catch { }

                return string.Format("连接成功。\r\n\r\n主机：{0}:{1}\r\n加密：{2}\r\n服务器问候：{3}",
                    host, port, note, FirstLine(greeting));
            }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return (i < 0 ? s : s.Substring(0, i)).Trim();
        }

        private static string LocalHostName()
        {
            try
            {
                string n = Dns.GetHostName();
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return "localhost";
        }

        private static Stream WrapSsl(Stream inner, string host)
        {
            RemoteCertificateValidationCallback cb = delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
            {
                return errs == SslPolicyErrors.None;
            };
            SslStream ssl = new SslStream(inner, false, cb);
            ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12 | SslProtocols.Tls11, true);
            return ssl;
        }

        private static string BuildMessage(string from, string to, string subject, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("From: ").Append(from).Append("\r\n");
            sb.Append("To: ").Append(to).Append("\r\n");
            sb.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
            sb.Append("Date: ").Append(DateTime.Now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("MIME-Version: 1.0\r\n");
            sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n");
            sb.Append("X-Mailer: PomoCC " + App.Version + "\r\n");
            sb.Append("\r\n");

            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
            for (int i = 0; i < b64.Length; i += 76)
            {
                int len = Math.Min(76, b64.Length - i);
                sb.Append(b64.Substring(i, len)).Append("\r\n");
            }
            return sb.ToString();
        }

        private static string EncodeHeader(string s)
        {
            if (s == null) s = "";
            return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
        }

        /// <summary>一次会话；按字节逐行读取，避免缓冲吃掉 STARTTLS 之后的握手数据。</summary>
        internal class SmtpSession        // internal：自检可以直接喂假响应验证状态码判断
        {
            public readonly Stream Stream;
            private readonly byte[] crlf = new byte[] { 13, 10 };
            private string lastResponse = "";

            public SmtpSession(Stream stream) { Stream = stream; }

            public string LastResponse { get { return lastResponse; } }

            /// <summary>
            /// 读一条（可能多行的）SMTP 响应，并**严格比较完整的三位状态码**。
            /// 只比第一位是不够的：例如等 250 时收到 220 会被误判成成功。
            /// </summary>
            public string ReadResponse(int expectCode, string what)
            {
                StringBuilder sb = new StringBuilder();
                string firstCode = null;
                while (true)
                {
                    string line = ReadLine();
                    if (line == null)
                        throw new IOException(string.Format("在等待「{0}」时连接被服务器关闭。已收到：{1}", what, sb.ToString().Trim()));
                    sb.Append(line).Append('\n');

                    // 多行响应的每一行都必须是「三位状态码 + 分隔符」的格式
                    if (line.Length < 3 || !IsThreeDigits(line.Substring(0, 3)))
                        throw new IOException(string.Format("服务器对「{0}」的响应格式不对：{1}", what, line.Trim()));

                    if (firstCode == null) firstCode = line.Substring(0, 3);
                    if (line.Length == 3 || line[3] == ' ') break;      // 最后一行
                    if (line[3] != '-')
                        throw new IOException(string.Format("服务器对「{0}」的响应格式不对：{1}", what, line.Trim()));
                }
                lastResponse = sb.ToString();

                string expect = expectCode.ToString();
                if (firstCode != expect)
                {
                    throw new IOException(string.Format("「{0}」期望服务器返回 {1}，实际返回 {2}：{3}",
                        what, expect, firstCode, lastResponse.Trim()));
                }
                return lastResponse;
            }

            private static bool IsThreeDigits(string s)
            {
                if (s == null || s.Length != 3) return false;
                for (int i = 0; i < 3; i++) if (s[i] < '0' || s[i] > '9') return false;
                return true;
            }

            public string Command(string cmd, int expectCode)
            {
                Write(cmd + "\r\n");
                ReadResponse(expectCode, cmd.Split(' ')[0]);
                return lastResponse;
            }

            public void Write(string text)
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                Stream.Write(data, 0, data.Length);
                Stream.Flush();
            }

            private string ReadLine()
            {
                MemoryStream ms = new MemoryStream();
                while (true)
                {
                    int b = Stream.ReadByte();
                    if (b < 0)
                    {
                        if (ms.Length == 0) return null;
                        break;
                    }
                    if (b == 10) break;
                    if (b == 13) continue;
                    ms.WriteByte((byte)b);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
