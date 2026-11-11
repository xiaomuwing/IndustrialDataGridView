using System;
using System.Windows.Forms;

namespace UniversalUtilities
{
    /// <summary>
    /// 表格列的定义：列名、数据类型，以及显示名、格式、对齐、宽度等可选属性。
    /// <see cref="IndustrialDataGridView{T}"/> 构造时读取全部字段；宽度类字段按 96 DPI 逻辑像素解释，
    /// 控件实际显示时按 <c>DeviceDpi / 96</c> 缩放。
    /// </summary>
    public struct ColumnStruct
    {
        /// <summary>
        /// 列的内部键，同时作为 <see cref="System.Data.DataColumn.ColumnName"/> 与
        /// <see cref="DataGridViewColumn.Name"/> / <see cref="DataGridViewColumn.DataPropertyName"/> 使用。
        /// 布局文件按这个键保存列宽和显隐状态，因此运行期不要修改。
        /// </summary>
        public string columnName;

        /// <summary>
        /// 列的数据类型，作为 <see cref="System.Data.DataColumn.DataType"/> 使用。
        /// 必须是 <see cref="System.Data.DataTable"/> 支持的类型（不能是 <see cref="Nullable{T}"/>）。
        /// </summary>
        public Type columnType;

        /// <summary>
        /// 表头显示文本（可带单位，如 <c>温度(℃)</c>）。
        /// 为 <see langword="null"/>、空串或空白时退回 <see cref="columnName"/>。
        /// </summary>
        public string headerText;

        /// <summary>
        /// 单元格显示格式（例如 <c>F2</c>、<c>N3</c>、<c>yyyy-MM-dd HH:mm:ss</c>）。
        /// 为 <see langword="null"/> 或空时不做格式化；布尔列忽略该字段。
        /// </summary>
        public string format;

        /// <summary>
        /// 单元格对齐方式。为 <see cref="DataGridViewContentAlignment.NotSet"/> 时按类型自动选择：
        /// 数值右对齐、布尔居中、其它左对齐。
        /// </summary>
        public DataGridViewContentAlignment alignment;

        /// <summary>列的初始宽度（96 DPI 逻辑像素）。小于等于 0 时使用默认宽度 100；实际显示按 DeviceDpi/96 缩放。</summary>
        public int width;

        /// <summary>列的最小宽度（96 DPI 逻辑像素）。小于等于 0 时使用 5；实际显示按 DeviceDpi/96 缩放。</summary>
        public int minimumWidth;

        /// <summary>
        /// 初始是否可见。<see langword="null"/> 或 <see langword="true"/> 表示可见，
        /// <see langword="false"/> 表示隐藏；控件最左侧 <c>LockedColumnCount</c> 列始终可见。
        /// </summary>
        public bool? visible;

        /// <summary>
        /// 是否允许用户在运行时点击列头排序。为 <see langword="true"/> 时该列
        /// <see cref="DataGridViewColumn.SortMode"/> 为 <see cref="DataGridViewColumnSortMode.Automatic"/>，
        /// 排序行为与 <see cref="DataGridView"/> 绑定 <see cref="System.Data.DataTable"/> 时一致；
        /// 为 <see langword="false"/> 时禁止点击排序。
        /// </summary>
        public bool sortable;
    }
}
