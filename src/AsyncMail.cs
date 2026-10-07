using System;
using System.Threading;
using System.Windows.Forms;

namespace PomoCC
{
    /// <summary>
    /// 手动发信类操作（连接测试 / 测试发信 / 重发）的统一异步外壳。
    ///
    /// 规则（对应任务清单第 3 组）：
    ///   · 网络动作一律在后台线程，界面不卡；
    ///   · 请求期间禁用按钮并显示「测试中…」，天然防止重复点击；
    ///   · 结果用 BeginInvoke 回到 UI 线程；
    ///   · 窗口已关闭就直接丢弃结果，绝不碰已释放的控件。
    /// </summary>
    internal static class AsyncMail
    {
        /// <param name="ui">用于回到 UI 线程的控件（通常是当前窗口）。</param>
        /// <param name="button">请求期间要禁用的按钮（可为 null）。</param>
        /// <param name="busyText">请求期间的按钮文字，例如「测试中…」。</param>
        /// <param name="idleText">结束后的按钮文字。</param>
        /// <param name="work">后台真正要做的事，返回"给用户看的成功信息"。</param>
        /// <param name="done">回到 UI 线程后的回调：ok + 信息（失败时是错误原因）。</param>
        public static void Run(Control ui, FlatButton button, string busyText, string idleText,
                               Func<string> work, Action<bool, string> done)
        {
            if (ui == null || work == null) return;

            SetButton(button, false, busyText);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool ok = false;
                string msg;
                try
                {
                    msg = work();
                    ok = true;
                }
                catch (Exception ex)
                {
                    msg = ex.Message;
                }

                // 窗口已经关了：结果直接丢掉，不回 UI
                if (ui.IsDisposed || !ui.IsHandleCreated) return;

                try
                {
                    ui.BeginInvoke(new Action(delegate
                    {
                        SetButton(button, true, idleText);
                        if (ui.IsDisposed) return;
                        if (done != null)
                        {
                            try { done(ok, msg); }
                            catch { }
                        }
                    }));
                }
                catch
                {
                    // 窗口在 BeginInvoke 之前被销毁：同样忽略
                }
            });
        }

        private static void SetButton(FlatButton button, bool enabled, string text)
        {
            if (button == null) return;
            try
            {
                button.Enabled = enabled;
                if (!string.IsNullOrEmpty(text)) button.Text = text;
                button.Invalidate();
            }
            catch { }
        }
    }
}
