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

namespace PomodoroSupervisor
{
    /// <summary>
    /// 自带 SMTP 客户端：同时支持 465（隐式 SSL）与 587/25（STARTTLS）。
    /// .NET 自带的 SmtpClient 不支持 465，国内邮箱大量使用 465，所以这里自己实现。
    /// </summary>
    public static class SmtpTransport
    {
        public static void Send(string host, int port, bool startTls, string user, string pass,
                                string from, string to, string subject, string body)
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(host, port);
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
            using (TcpClient client = new TcpClient())
            {
                client.Connect(host, port);
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
            sb.Append("X-Mailer: PomodoroSupervisor\r\n");
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
        private class SmtpSession
        {
            public readonly Stream Stream;
            private readonly byte[] crlf = new byte[] { 13, 10 };
            private string lastResponse = "";

            public SmtpSession(Stream stream) { Stream = stream; }

            public string LastResponse { get { return lastResponse; } }

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
                    if (line.Length >= 3)
                    {
                        if (firstCode == null) firstCode = line.Substring(0, 3);
                        if (line.Length == 3 || line[3] == ' ') break;
                    }
                    else break;
                }
                lastResponse = sb.ToString();
                if (firstCode == null || firstCode[0] != char.Parse(expectCode.ToString().Substring(0, 1)))
                    throw new IOException(string.Format("服务器拒绝了「{0}」：{1}", what, lastResponse.Trim()));
                return lastResponse;
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
