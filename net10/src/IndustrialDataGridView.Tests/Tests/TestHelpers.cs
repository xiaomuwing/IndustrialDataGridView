using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UniversalUtilities.Tests
{
    /// <summary>测试公共设施（临时目录、STA 线程、端口分配、浮点比较）。</summary>
    internal static class TestHelpers
    {
        /// <summary>
        /// 在 STA 线程上执行动作（WinForms/GDI 相关测试必须这样跑），
        /// 线程内的异常会原样抛回到测试方法。
        /// </summary>
        public static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromMinutes(2)))
            {
                throw new AssertFailedException("STA 测试线程超时未结束。");
            }
            if (failure != null)
            {
                throw new AssertFailedException("STA 线程内失败：" + failure.Message, failure);
            }
        }

        /// <summary>创建本次测试专用的临时目录（记得用 DeleteDirectory 清理）。</summary>
        public static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uutests_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响测试结论（临时目录由系统回收）
            }
        }

        /// <summary>取一个当前空闲的 TCP 端口（Modbus/网络类测试用）。</summary>
        public static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>浮点近似比较（相对+绝对容差）。</summary>
        public static void AssertClose(double expected, double actual, double tolerance = 1e-9, string message = null)
        {
            if (double.IsNaN(expected) && double.IsNaN(actual))
            {
                return;
            }

            double diff = Math.Abs(expected - actual);
            double scale = Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
            if (diff > tolerance * scale)
            {
                Assert.Fail((message ?? "数值不一致") + "：期望 " + expected.ToString("R")
                    + "，实际 " + actual.ToString("R") + "（相对差 " + (diff / scale).ToString("E2") + "）");
            }
        }

        public static void AssertSequenceClose(double[] expected, double[] actual, double tolerance = 1e-9, string message = null)
        {
            Assert.IsNotNull(actual, message ?? "结果不应为 null");
            Assert.AreEqual(expected.Length, actual.Length, (message ?? "长度不一致"));
            for (int i = 0; i < expected.Length; i++)
            {
                AssertClose(expected[i], actual[i], tolerance, (message ?? "序列") + "[索引 " + i + "]");
            }
        }

        /// <summary>断言动作抛出指定异常，返回该异常实例（便于继续检查消息/字段）。</summary>
        public static T AssertThrows<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                Assert.Fail("期望抛出 " + typeof(T).Name + "，实际抛出 " + ex.GetType().Name + "：" + ex.Message);
            }

            Assert.Fail("期望抛出 " + typeof(T).Name + "，实际没有抛异常。");
            return null;
        }

        /// <summary>轮询等待条件成立（避免用固定 sleep 造成偶发失败）。</summary>
        public static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000, int pollMs = 20)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(pollMs);
            }
            return condition();
        }
    }
}
