using System;
using System.IO;
using System.Net;
using System.Text;

namespace PomoCC
{
    /// <summary>四种 HTTP 发信通道，都是免费额度可用的。</summary>
    public static class HttpSender
    {
        public static void Send(Settings s, string subject, string body)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            string url;
            string json;
            string authHeader;
            string authValue;

            string key = s.GetApiKey();
            string from = s.SenderEmail;
            string to = s.SupervisorEmail;

            // 协议边界再校验一次：不能只依赖设置界面，直接调用发送入口也必须拦住
            string why;
            if (!SettingsValidator.IsValidEmail(from, out why))
                throw new InvalidOperationException("发件邮箱不合法：" + why + "。");
            if (!SettingsValidator.IsValidEmail(to, out why))
                throw new InvalidOperationException("收件邮箱不合法：" + why + "。");

            switch (s.SendMode)
            {
                case "resend":
                    url = "https://api.resend.com/emails";
                    json = "{\"from\":" + J(from)
                         + ",\"to\":[" + J(to) + "]"
                         + ",\"subject\":" + J(subject)
                         + ",\"text\":" + J(body) + "}";
                    authHeader = "Authorization";
                    authValue = "Bearer " + key;
                    break;

                case "sendgrid":
                    url = "https://api.sendgrid.com/v3/mail/send";
                    json = "{\"personalizations\":[{\"to\":[{\"email\":" + J(to) + "}]}]"
                         + ",\"from\":{\"email\":" + J(from) + "}"
                         + ",\"subject\":" + J(subject)
                         + ",\"content\":[{\"type\":\"text/plain\",\"value\":" + J(body) + "}]}";
                    authHeader = "Authorization";
                    authValue = "Bearer " + key;
                    break;

                case "brevo":
                    url = "https://api.brevo.com/v3/smtp/email";
                    json = "{\"sender\":{\"email\":" + J(from) + "}"
                         + ",\"to\":[{\"email\":" + J(to) + "}]"
                         + ",\"subject\":" + J(subject)
                         + ",\"textContent\":" + J(body) + "}";
                    authHeader = "api-key";
                    authValue = key;
                    break;

                default: // custom
                    url = s.HttpUrl;
                    if (string.IsNullOrEmpty(url))
                        throw new InvalidOperationException("没有填写自定义接口地址。");
                    // 明文 http 只允许本机；不允许地址里带凭据或把 Key 写在参数上
                    if (!SettingsValidator.IsAllowedHttpUrl(url, out why))
                        throw new InvalidOperationException("自定义接口地址不合法：" + why + "。");
                    json = "{\"from\":" + J(from)
                         + ",\"to\":" + J(to)
                         + ",\"subject\":" + J(subject)
                         + ",\"text\":" + J(body) + "}";
                    authHeader = "Authorization";
                    authValue = "Bearer " + key;
                    break;
            }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.UserAgent = "PomoCC/" + App.Version;
            // 请求带认证头：绝不允许自动跟随重定向，否则 Authorization 会被发到未校验的地址
            req.AllowAutoRedirect = false;
            if (!string.IsNullOrEmpty(authValue) && authValue != "Bearer ")
                req.Headers[authHeader] = authValue;

            byte[] payload = Encoding.UTF8.GetBytes(json);
            req.ContentLength = payload.Length;
            using (Stream st = req.GetRequestStream())
            {
                st.Write(payload, 0, payload.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                        throw new IOException(RedirectNote(code));
                    if (code < 200 || code > 299)
                        throw new IOException(string.Format("发信接口返回了非成功状态码 {0}。", code));

                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string text = sr.ReadToEnd();
                        if (text.Length > 400) text = text.Substring(0, 400);
                        Store.Log(string.Format("HTTP 发信成功：{0} {1} {2}", (int)resp.StatusCode, url, text));
                    }
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse wr = wex.Response as HttpWebResponse;
                if (wr != null && (int)wr.StatusCode >= 300 && (int)wr.StatusCode < 400)
                    throw new IOException(RedirectNote((int)wr.StatusCode));

                string detail = "";
                if (wex.Response != null)
                {
                    try
                    {
                        using (StreamReader sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            detail = sr.ReadToEnd();
                        }
                    }
                    catch { }
                }
                if (detail.Length > 400) detail = detail.Substring(0, 400);
                throw new IOException(string.Format("发信接口返回错误：{0} {1}", wex.Message, detail));
            }
        }

        /// <summary>重定向一律不跟随：明确告诉用户把地址填成最终地址。</summary>
        private static string RedirectNote(int code)
        {
            return string.Format("发信接口要求重定向（{0}）。出于安全考虑，本程序不会带着认证信息跟随重定向，" +
                "请把接口地址直接填成最终地址。", code);
        }

        /// <summary>JSON 字符串转义。</summary>
        private static string J(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }
    }
}
