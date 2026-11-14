namespace UniversalUtilities
{
    /// <summary>
    /// 独立解决方案中的日志空实现：控件只把布局/单元格错误交给宿主日志系统，
    /// 本解决方案不引入 UniversalUtilities 的 Logger，避免额外依赖。
    /// </summary>
    internal static class Log
    {
        internal static void WriteLog(string message, string module, string category)
        {
            // 独立交付版不落盘；调用方可通过 LastLayoutError / LastCellUpdateError / 失败计数获取错误。
        }
    }
}
