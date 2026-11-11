using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace UniversalUtilities
{
    /// <summary>
    /// 工业现场采集用的数据列表控件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 设计约束：
    /// 数据列在构造时固定、不允许排序、不允许用户编辑；每控件一个布局文件；
    /// 初始行集合用 <see cref="ShowRows"/> 建立，运行期可用 <see cref="AppendRow"/> / <see cref="InsertRow"/>
    /// / <see cref="RemoveRow(int)"/> 增删行；全量重建（<see cref="ShowRows"/> / <see cref="ClearRows"/>）
    /// 只能在控件不可见时执行。
    /// 之后只通过 <see cref="SetCell(int,int,object)"/>、<see cref="ApplyUpdates"/>
    /// 或线程安全的 <see cref="PostUpdates"/> 修改变化了的单元格。
    /// </para>
    /// <para>
    /// 选中行通过 <see cref="GetSelectedItems"/> / <see cref="SelectedItems"/> 直接取回对应的
    /// <typeparamref name="T"/> 列表；增删行后映射自动维护。
    /// </para>
    /// <para>
    /// 列顺序可由用户在运行时拖拽调整，布局文件按显示顺序保存并在下次加载时恢复；
    /// <see cref="LockedColumnCount"/> 指定的最左侧 N 列会冻结且始终可见，不参与换序和隐藏。
    /// 每列是否允许点击列头排序由 <see cref="ColumnStruct.sortable"/> 控制。
    /// 排序后显示顺序与数据顺序不再一致，<see cref="CellUpdate"/> 的行号仍为 DataTable 数据行号；
    /// 只刷新可见区的调用方应使用 <see cref="GetVisibleDataRowIndexes"/> 把显示行映射为数据行。
    /// </para>
    /// <para>
    /// 线程契约：
    /// 除 <see cref="PostUpdates"/> / <see cref="PostCellUpdate"/> 外，所有公开的写操作都必须在
    /// 创建控件的 UI 线程调用，否则抛 <see cref="InvalidOperationException"/>；
    /// 后台线程用 Post 系列方法入队，由控件合并到下一次 UI 消息中应用。
    /// </para>
    /// <para>
    /// 控件不内置右键菜单、列选择器或导出；列显隐、布局和业务菜单由调用方统一组织，
    /// 通过 <see cref="ShowColumn"/> / <see cref="HideColumn"/> / <see cref="ResetLayout"/> /
    /// <see cref="SaveLayout"/> / <see cref="LoadLayout"/> 等接口操作。
    /// </para>
    /// <para>
    /// 行列上限：<see cref="MaxRowCount"/>（1000）与 <see cref="MaxColumnCount"/>（256）。
    /// 字号限制：<see cref="MinFontSize"/>（6pt）到 <see cref="MaxFontSize"/>（72pt），始终使用常规字形。
    /// 表头高度按字体/DPI 手动计算（列头高度不可拖动），行高只调整当前可见行，不可见行在滚动进入可见区时补齐。
    /// </para>
    /// <para>
    /// 布局文件为带版本号的 JSON，按 <see cref="ColumnStruct.columnName"/> 保存列宽（96 DPI 逻辑像素）与显隐；
    /// 加载时按 <c>DeviceDpi / 96</c> 缩放；第一列始终强制可见；文件损坏或版本不支持时回退为“全部显示 + 默认列宽”。
    /// 旧的“列宽 JSON + 显隐 CSV”可用 <see cref="MigrateLegacyLayout"/> 迁移。
    /// </para>
    /// </remarks>
    /// <typeparam name="T">每行承载的数据类型；派生类实现 <see cref="GetRowValues"/> 完成映射。</typeparam>
    public abstract class IndustrialDataGridView<T> : DataGridView
    {
        /// <summary>允许的最大数据行数。</summary>
        public const int MaxRowCount = 1000;

        /// <summary>允许的最大列数。</summary>
        public const int MaxColumnCount = 256;

        /// <summary>允许的最小字号（磅）。</summary>
        public const float MinFontSize = 6f;

        /// <summary>允许的最大字号（磅）。</summary>
        public const float MaxFontSize = 72f;

        /// <summary>默认字号（磅）。</summary>
        public const float DefaultFontSize = 10.5f;

        /// <summary>列的默认宽度（96 DPI 逻辑像素）。</summary>
        public const int DefaultColumnWidth = 100;

        /// <summary>列的默认最小宽度（像素）。</summary>
        public const int DefaultMinimumColumnWidth = 5;

        private const int RowHeightPadding = 5;
        private const int HeaderHeightPadding = 6;
        private const int LayoutFileVersion = 2;
        private const int LogicalDpi = 96;
        private const int ColumnWidthSaveDelayMs = 500;
        private const int MaxPendingUpdates = 100000;
        private const long CellErrorLogIntervalTicks = 5 * TimeSpan.TicksPerSecond;

        private readonly DataTable dataTable;
        private readonly ColumnStruct[] columnDefinitions;
        private readonly IReadOnlyList<ColumnStruct> columnDefinitionsView;
        private readonly Dictionary<string, int> columnIndexByKey;
        private readonly int ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly System.Windows.Forms.Timer columnWidthSaveTimer = new System.Windows.Forms.Timer();
        private readonly ConcurrentQueue<CellUpdate> pendingUpdates = new ConcurrentQueue<CellUpdate>();
        private readonly List<T> rowItems = new List<T>();
        private readonly Dictionary<DataRow, T> rowMap = new Dictionary<DataRow, T>();
        private readonly List<DataGridViewColumn> lockedColumns = new List<DataGridViewColumn>();
        private readonly Dictionary<string, int> baseColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private Font appliedFont;
        private bool columnWidthSavePending;
        private bool loadingLayout;
        private bool savingLayout;
        private bool layoutWidthsChangedByUser;
        private int widthsDpi = LogicalDpi;
        private bool disposed;
        private bool mutatingRows;
        private bool enforcingLockedColumns;
        private bool autoFittingWidths;
        private string alarmColumnKey;
        private int alarmColumnIndex = -1;
        private Color alarmRowBackColor = Color.Red;
        private Color alarmRowForeColor = Color.White;
        private Color alarmRowSelectedBackColor = Color.Empty;
        private Color alarmRowSelectedForeColor = Color.Empty;
        private int lockedColumnCount = 1;
        private int pendingUpdateCount;
        private int drainScheduled;
        private long droppedUpdateCount;
        private long failedCellUpdateCount;
        private long lastCellErrorLogTicks;
        private string lastCellUpdateError;
        private string lastLayoutError;
        private LayoutFileModel loadedLayout;
        private int loadedLayoutDpi;

        /// <summary>
        /// 构造函数：按列定义建立 DataTable 与显示列，设置外观、双缓冲、字号与行高。
        /// </summary>
        /// <param name="columns">列定义（列名、类型以及可选的显示名/格式/对齐/宽度等），构造时复制一份，之后外部修改不影响控件。</param>
        /// <param name="background">背景色，同时用于表头与单元格底色。</param>
        /// <param name="selectionColor">选中行的背景色。</param>
        /// <exception cref="ArgumentNullException"><paramref name="columns"/> 为 <see langword="null"/>。</exception>
        /// <exception cref="ArgumentException">
        /// 列数超过 <see cref="MaxColumnCount"/>；列名为空或重复；列类型为空；
        /// 或列类型不被 <see cref="DataTable"/> 支持（例如 <see cref="Nullable{T}"/>）。
        /// </exception>
        protected IndustrialDataGridView(IReadOnlyList<ColumnStruct> columns, Color background, Color selectionColor)
        {
            if (columns == null)
            {
                throw new ArgumentNullException(nameof(columns));
            }
            if (columns.Count > MaxColumnCount)
            {
                throw new ArgumentException("列数 " + columns.Count + " 超过上限 " + MaxColumnCount + "。", nameof(columns));
            }

            columnDefinitions = new ColumnStruct[columns.Count];
            columnIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++)
            {
                ColumnStruct definition = columns[i];
                if (string.IsNullOrWhiteSpace(definition.columnName))
                {
                    throw new ArgumentException("第 " + i + " 列没有列名。", nameof(columns));
                }
                if (columnIndexByKey.ContainsKey(definition.columnName))
                {
                    throw new ArgumentException("列名重复: " + definition.columnName, nameof(columns));
                }
                if (definition.columnType == null)
                {
                    throw new ArgumentException("列 " + definition.columnName + " 没有数据类型。", nameof(columns));
                }

                columnDefinitions[i] = definition;
                columnIndexByKey.Add(definition.columnName, i);
            }
            columnDefinitionsView = Array.AsReadOnly(columnDefinitions);

            dataTable = CreateDataTable(columnDefinitions);
            InitializeGrid(background, selectionColor);
            CreateColumns();
            InitializeLockedColumns();
            DataSource = dataTable;
            ForceFirstColumnVisible();
            ApplyLockedColumnState();
            UpdateRowHeights();
            AutoFitColumnHeaders();

            columnWidthSaveTimer.Interval = ColumnWidthSaveDelayMs;
            columnWidthSaveTimer.Tick += ColumnWidthSaveTimerTick;
            ColumnWidthChanged += OnColumnWidthChangedInternal;
            CellFormatting += OnCellFormattingInternal;
            ColumnDisplayIndexChanged += OnColumnDisplayIndexChangedInternal;
            ColumnStateChanged += OnColumnStateChangedInternal;
            Scroll += OnScrollUpdateRowHeights;
            Resize += OnResizeUpdateRowHeights;
        }

        /// <summary>构造时传入的列定义（只读视图，顺序与 <see cref="DataGridView.Columns"/> / <see cref="Table"/> 的列顺序一致）。</summary>
        public IReadOnlyList<ColumnStruct> ColumnDefinitions
        {
            get { return columnDefinitionsView; }
        }

        /// <summary>
        /// 从左侧第一列开始锁定的列数，默认 1。锁定列固定显示在左侧（Frozen=true）、始终可见，
        /// 且不允许用户拖拽换序；运行时换序只作用于未锁定列。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">值小于 0 或大于当前列数。</exception>
        public int LockedColumnCount
        {
            get { return lockedColumnCount; }
            set
            {
                EnsureUiThread();
                if (value < 0 || value > Columns.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "锁定列数必须在 0.." + Columns.Count + " 之间。");
                }
                if (lockedColumnCount == value && lockedColumns.Count == value)
                {
                    return;
                }

                lockedColumnCount = value;
                InitializeLockedColumns();
                ApplyLockedColumns();
                ScheduleLayoutSave();
            }
        }

        /// <summary>判断某列当前是否属于锁定列（UI 线程）。</summary>
        /// <param name="columnKey">列键。</param>
        /// <returns>是锁定列返回 <see langword="true"/>。</returns>
        public bool IsColumnLocked(string columnKey)
        {
            EnsureUiThread();
            if (string.IsNullOrEmpty(columnKey))
            {
                return false;
            }
            foreach (DataGridViewColumn column in lockedColumns)
            {
                if (string.Equals(column.Name, columnKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 报警状态列键。该列为 <see langword="true"/> 的行会使用 <see cref="AlarmRowBackColor"/> /
        /// <see cref="AlarmRowForeColor"/> 绘制；为空时不启用报警行着色。
        /// </summary>
        public string AlarmColumnKey
        {
            get { return alarmColumnKey; }
            set
            {
                EnsureUiThread();
                if (string.Equals(alarmColumnKey, value, StringComparison.Ordinal))
                {
                    return;
                }
                alarmColumnKey = value;
                alarmColumnIndex = GetColumnIndex(value);
                Invalidate();
            }
        }

        /// <summary>报警行背景色，默认 <see cref="Color.Red"/>。</summary>
        public Color AlarmRowBackColor
        {
            get { return alarmRowBackColor; }
            set
            {
                EnsureUiThread();
                if (alarmRowBackColor == value)
                {
                    return;
                }
                alarmRowBackColor = value;
                Invalidate();
            }
        }

        /// <summary>报警行前景色，默认 <see cref="Color.White"/>（与红色背景对比清晰）。</summary>
        public Color AlarmRowForeColor
        {
            get { return alarmRowForeColor; }
            set
            {
                EnsureUiThread();
                if (alarmRowForeColor == value)
                {
                    return;
                }
                alarmRowForeColor = value;
                Invalidate();
            }
        }

        /// <summary>
        /// 报警行被选中时的选中背景色。默认为 <see cref="Color.Empty"/>，表示沿用控件的普通选中背景色，
        /// 从而保留“已选中”的视觉反馈；可设置为深红等颜色以形成“报警 + 选中”的组合。
        /// </summary>
        public Color AlarmRowSelectedBackColor
        {
            get { return alarmRowSelectedBackColor; }
            set
            {
                EnsureUiThread();
                if (alarmRowSelectedBackColor == value)
                {
                    return;
                }
                alarmRowSelectedBackColor = value;
                Invalidate();
            }
        }

        /// <summary>
        /// 报警行被选中时的选中前景色。默认为 <see cref="Color.Empty"/>，表示沿用控件的普通选中前景色。
        /// </summary>
        public Color AlarmRowSelectedForeColor
        {
            get { return alarmRowSelectedForeColor; }
            set
            {
                EnsureUiThread();
                if (alarmRowSelectedForeColor == value)
                {
                    return;
                }
                alarmRowSelectedForeColor = value;
                Invalidate();
            }
        }

        /// <summary>判断指定数据行当前是否处于报警状态（UI 线程；行号是 DataTable 数据顺序）。</summary>
        /// <param name="rowIndex">数据行索引。</param>
        /// <returns>报警列为真时返回 <see langword="true"/>。</returns>
        public bool IsAlarmRow(int rowIndex)
        {
            EnsureUiThread();
            ValidateRowIndex(rowIndex);
            return IsAlarmRowCore(dataTable.Rows[rowIndex]);
        }

        private bool IsAlarmRowCore(DataRow row)
        {
            if (alarmColumnIndex < 0 || row == null)
            {
                return false;
            }

            object value = row[alarmColumnIndex];
            if (value == null || value == DBNull.Value)
            {
                return false;
            }
            if (value is bool)
            {
                return (bool)value;
            }
            if (value is string)
            {
                bool parsed;
                return bool.TryParse((string)value, out parsed) && parsed;
            }
            try
            {
                return Convert.ToInt64(value) != 0L;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 控件内部的 <see cref="DataTable"/>。派生类可直接读取；修改请使用
        /// <see cref="SetCell(int,int,object)"/> / <see cref="ApplyUpdates"/> / <see cref="PostUpdates"/>，
        /// 不要直接改行集合（会绕过上限与线程检查）。
        /// </summary>
        public DataTable Table
        {
            get { return dataTable; }
        }

        /// <summary>
        /// 布局文件路径（由调用方提供）。为空时不读也不写布局。
        /// 构造之后设置本属性不会自动读取，需要调用 <see cref="LoadLayout"/>。
        /// </summary>
        public string LayoutFile { get; set; }

        /// <summary>最近一次布局读写/迁移的错误信息；成功时为 <see langword="null"/>。</summary>
        public string LastLayoutError
        {
            get { return lastLayoutError; }
            private set { lastLayoutError = value; }
        }

        /// <summary>最近一次单元格写入失败的错误信息；成功写入不会清除它。</summary>
        public string LastCellUpdateError
        {
            get { return lastCellUpdateError; }
            private set { lastCellUpdateError = value; }
        }

        /// <summary>累计写入失败的单元格次数。</summary>
        public long FailedCellUpdateCount
        {
            get { return Interlocked.Read(ref failedCellUpdateCount); }
        }

        /// <summary>因队列已满而丢弃的增量更新条数。</summary>
        public long DroppedUpdateCount
        {
            get { return Interlocked.Read(ref droppedUpdateCount); }
        }

        /// <summary>
        /// 派生类实现：把一行数据映射为按 <see cref="ColumnDefinitions"/> 顺序排列的值数组。
        /// </summary>
        /// <param name="item">一行数据。</param>
        /// <returns>
        /// 长度必须等于列数的数组；元素为 <see langword="null"/> 时按 <see cref="DBNull"/> 处理。
        /// 每次调用都应返回新数组（控件会再复制一份，避免多行共用同一数组）。
        /// </returns>
        protected abstract object[] GetRowValues(T item);

        /// <summary>
        /// 全量重建：清空现有行，按 <paramref name="rows"/> 的顺序重新填充。
        /// </summary>
        /// <param name="rows">新的行集合；数量不能超过 <see cref="MaxRowCount"/>。</param>
        /// <exception cref="ArgumentNullException"><paramref name="rows"/> 为 <see langword="null"/>。</exception>
        /// <exception cref="ArgumentException">行数超过 <see cref="MaxRowCount"/>。</exception>
        /// <exception cref="InvalidOperationException">
        /// 控件在已显示的窗体上且当前可见；或派生类的 <see cref="GetRowValues"/> 返回的数组长度不对。
        /// </exception>
        /// <remarks>
        /// 全量重建会重置选中行与滚动位置，这是本控件的既定语义；常规 0.1 秒刷新请勿调用本方法。
        /// </remarks>
        public void ShowRows(IReadOnlyList<T> rows)
        {
            EnsureUiThread();
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }
            if (rows.Count > MaxRowCount)
            {
                throw new ArgumentException("行数 " + rows.Count + " 超过上限 " + MaxRowCount + "。", nameof(rows));
            }
            if (IsReallyVisible())
            {
                throw new InvalidOperationException("全量重建不能在控件可见时执行；请先隐藏控件或窗体，或改用 SetCell/ApplyUpdates 做增量刷新。");
            }

            var valuesList = new List<object[]>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                valuesList.Add(GetRowValuesChecked(rows[i], i));
            }

            SuspendLayout();
            mutatingRows = true;
            try
            {
                DataSource = null;
                try
                {
                    dataTable.BeginLoadData();
                    try
                    {
                        dataTable.Rows.Clear();
                        rowMap.Clear();
                        rowItems.Clear();
                        for (int i = 0; i < valuesList.Count; i++)
                        {
                            DataRow dataRow = dataTable.Rows.Add(valuesList[i]);
                            rowMap[dataRow] = rows[i];
                            rowItems.Add(rows[i]);
                        }
                    }
                    finally
                    {
                        dataTable.EndLoadData();
                    }
                }
                finally
                {
                    DataSource = dataTable;
                }
            }
            finally
            {
                mutatingRows = false;
                ResumeLayout(true);
            }
            ForceFirstColumnVisible();
        }

        /// <summary>
        /// 追加一行（不触发全量重建，允许控件可见时调用）。行对应的 <typeparamref name="T"/> 会一并保存，
        /// 之后可通过 <see cref="GetItemAt"/> 或 <see cref="GetSelectedItems"/> 取回。
        /// </summary>
        /// <param name="item">要追加的数据。</param>
        /// <exception cref="InvalidOperationException">已达到 <see cref="MaxRowCount"/>，或 GetRowValues 返回值不合法。</exception>
        public void AppendRow(T item)
        {
            EnsureUiThread();
            if (dataTable.Rows.Count >= MaxRowCount)
            {
                throw new InvalidOperationException("已达到最大行数 " + MaxRowCount + "。");
            }

            object[] values = GetRowValuesChecked(item, dataTable.Rows.Count);
            mutatingRows = true;
            try
            {
                DataRow dataRow = dataTable.Rows.Add(values);
                rowItems.Add(item);
                rowMap[dataRow] = item;
            }
            finally
            {
                mutatingRows = false;
            }
        }

        /// <summary>
        /// 在指定位置插入一行（允许控件可见时调用），行数加一；原有行及其 <typeparamref name="T"/> 映射顺延。
        /// </summary>
        /// <param name="rowIndex">插入位置（DataTable 数据顺序，不受列排序影响），0 到当前行数（含）。</param>
        /// <param name="item">要插入的数据。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> 不在 0..当前行数 范围内。</exception>
        /// <exception cref="InvalidOperationException">已达到 <see cref="MaxRowCount"/>，或 GetRowValues 返回值不合法。</exception>
        public void InsertRow(int rowIndex, T item)
        {
            EnsureUiThread();
            if (rowIndex < 0 || rowIndex > dataTable.Rows.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "行索引必须在 0.." + dataTable.Rows.Count + " 之间。");
            }
            if (dataTable.Rows.Count >= MaxRowCount)
            {
                throw new InvalidOperationException("已达到最大行数 " + MaxRowCount + "。");
            }

            object[] values = GetRowValuesChecked(item, rowIndex);
            DataRow dataRow = dataTable.NewRow();
            dataRow.ItemArray = values;
            mutatingRows = true;
            try
            {
                dataTable.Rows.InsertAt(dataRow, rowIndex);
                rowItems.Insert(rowIndex, item);
                rowMap[dataRow] = item;
            }
            finally
            {
                mutatingRows = false;
            }
        }

        /// <summary>
        /// 删除指定行（允许控件可见时调用），行数减一；其后的行索引和 <typeparamref name="T"/> 映射前移。
        /// </summary>
        /// <param name="rowIndex">要删除的数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> 越界。</exception>
        public void RemoveRow(int rowIndex)
        {
            EnsureUiThread();
            ValidateRowIndex(rowIndex);
            if (rowIndex >= rowItems.Count)
            {
                throw new InvalidOperationException("行映射未同步；请通过本控件的行操作方法修改行集合。");
            }

            mutatingRows = true;
            try
            {
                DataRow dataRow = dataTable.Rows[rowIndex];
                dataTable.Rows.RemoveAt(rowIndex);
                rowItems.RemoveAt(rowIndex);
                rowMap.Remove(dataRow);
            }
            finally
            {
                mutatingRows = false;
            }
        }

        /// <summary>
        /// 按对象删除第一处匹配的行（引用/默认相等比较），成功返回 <see langword="true"/>；没找到返回 <see langword="false"/>。
        /// </summary>
        /// <param name="item">要删除的行对象。</param>
        /// <returns>删除成功返回 <see langword="true"/>。</returns>
        public bool RemoveRow(T item)
        {
            EnsureUiThread();
            int index = rowItems.IndexOf(item);
            if (index < 0)
            {
                return false;
            }

            RemoveRow(index);
            return true;
        }

        /// <summary>取指定行对应的 <typeparamref name="T"/> 对象（UI 线程）。</summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <returns>该行最初加入控件时的对象实例。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> 越界。</exception>
        /// <exception cref="InvalidOperationException">行映射未同步。</exception>
        public T GetItemAt(int rowIndex)
        {
            EnsureUiThread();
            ValidateRowIndex(rowIndex);
            if (rowIndex >= rowItems.Count)
            {
                throw new InvalidOperationException("行映射未同步；请通过本控件的行操作方法修改行集合。");
            }
            return rowItems[rowIndex];
        }

        /// <summary>用一条数据覆盖指定行的全部单元格（行数不变；只改变化单元格请用 <see cref="SetCell(int,int,object)"/>）。</summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <param name="item">新的行数据。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> 越界。</exception>
        /// <exception cref="InvalidOperationException">GetRowValues 返回值不合法，或行映射未同步。</exception>
        public void SetRow(int rowIndex, T item)
        {
            EnsureUiThread();
            ValidateRowIndex(rowIndex);
            if (rowIndex >= rowItems.Count)
            {
                throw new InvalidOperationException("行映射未同步；请通过本控件的行操作方法修改行集合。");
            }

            object[] values = GetRowValuesChecked(item, rowIndex);
            DataRow dataRow = dataTable.Rows[rowIndex];
            dataRow.ItemArray = values;
            rowItems[rowIndex] = item;
            rowMap[dataRow] = item;
            if (alarmColumnIndex >= 0)
            {
                Invalidate();
            }
        }

        /// <summary>
        /// 取当前用户在控件中选中的行对应的 <typeparamref name="T"/> 列表（UI 线程）。
        /// 默认 <see cref="DataGridView.SelectionMode"/> 为整行选择；也兼容单元格选择模式。
        /// 返回按当前显示顺序排序、按行去重后的快照，排序后仍能返回正确的对象。
        /// </summary>
        /// <returns>选中行对应的对象列表；没有选中时返回空列表。</returns>
        public IReadOnlyList<T> GetSelectedItems()
        {
            EnsureUiThread();
            var selected = new List<T>();
            if (mutatingRows || rowMap.Count == 0)
            {
                return selected;
            }

            if (SelectedRows.Count > 0)
            {
                var rows = new List<DataGridViewRow>(SelectedRows.Count);
                foreach (DataGridViewRow row in SelectedRows)
                {
                    rows.Add(row);
                }
                rows.Sort((a, b) => a.Index.CompareTo(b.Index));
                foreach (DataGridViewRow row in rows)
                {
                    T item;
                    if (TryGetItemForDisplayRow(row, out item))
                    {
                        selected.Add(item);
                    }
                }
            }
            else if (SelectedCells.Count > 0)
            {
                var indexes = new SortedSet<int>();
                foreach (DataGridViewCell cell in SelectedCells)
                {
                    if (cell.RowIndex >= 0 && cell.RowIndex < Rows.Count)
                    {
                        indexes.Add(cell.RowIndex);
                    }
                }
                foreach (int index in indexes)
                {
                    T item;
                    if (TryGetItemForDisplayRow(Rows[index], out item))
                    {
                        selected.Add(item);
                    }
                }
            }
            return selected;
        }

        private bool TryGetItemForDisplayRow(DataGridViewRow row, out T item)
        {
            item = default(T);
            if (row == null)
            {
                return false;
            }

            DataRowView view = row.DataBoundItem as DataRowView;
            if (view != null)
            {
                return rowMap.TryGetValue(view.Row, out item);
            }

            if (row.Index >= 0 && row.Index < rowItems.Count)
            {
                item = rowItems[row.Index];
                return true;
            }
            return false;
        }

        /// <summary>同 <see cref="GetSelectedItems"/>；属性形式便于调用方直接读取。</summary>
        public IReadOnlyList<T> SelectedItems
        {
            get { return GetSelectedItems(); }
        }

        /// <summary>
        /// 清空全部行。与 <see cref="ShowRows"/> 一样，只允许在控件不可见时调用。
        /// </summary>
        /// <exception cref="InvalidOperationException">控件当前可见。</exception>
        public void ClearRows()
        {
            EnsureUiThread();
            if (IsReallyVisible())
            {
                throw new InvalidOperationException("清空行不能在控件可见时执行；请先隐藏控件或窗体。");
            }

            SuspendLayout();
            mutatingRows = true;
            try
            {
                DataSource = null;
                try
                {
                    dataTable.Rows.Clear();
                    rowItems.Clear();
                    rowMap.Clear();
                }
                finally
                {
                    DataSource = dataTable;
                }
            }
            finally
            {
                mutatingRows = false;
                ResumeLayout(true);
            }
            ForceFirstColumnVisible();
        }

        /// <summary>
        /// 按列键（<see cref="ColumnStruct.columnName"/>）取列索引；找不到返回 -1。
        /// 可在任意线程调用（列定义构造后不再变化），适合后台线程构造 <see cref="CellUpdate"/>。
        /// </summary>
        /// <param name="columnKey">列键。</param>
        /// <returns>列索引；不存在时为 -1。</returns>
        public int GetColumnIndex(string columnKey)
        {
            if (string.IsNullOrEmpty(columnKey))
            {
                return -1;
            }

            int index;
            return columnIndexByKey.TryGetValue(columnKey, out index) ? index : -1;
        }

        private DataGridViewColumn FindColumnByKey(string columnKey)
        {
            if (string.IsNullOrEmpty(columnKey))
            {
                return null;
            }
            foreach (DataGridViewColumn column in Columns)
            {
                if (string.Equals(column.Name, columnKey, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }
            return null;
        }

        /// <summary>
        /// 取当前可见行范围（UI 线程）。范围包含上下各 1 行的余量，避免滚动边缘漏刷。
        /// 只在控件已创建句柄且有数据行时返回 <see langword="true"/>。
        /// </summary>
        /// <param name="firstRowIndex">可见范围的第一行显示索引（含；排序后不等于 DataTable 数据行索引）。</param>
        /// <param name="rowCount">可见范围的行数。</param>
        /// <returns>能确定可见范围时返回 <see langword="true"/>；控件未显示或没有数据行时返回 <see langword="false"/>。</returns>
        /// <remarks>
        /// 仅刷新可见行的调用方可用本方法过滤 <see cref="CellUpdate"/>；但 <see cref="CellUpdate"/> 的行号是
        /// DataTable 数据行号，列排序后应改用 <see cref="GetVisibleDataRowIndexes"/> 取数据行索引。
        /// 不可见行的旧值需要在滚动时用当前数据补刷。后台线程不能直接调用本方法，请在 UI 线程取快照后传给后台线程。
        /// </remarks>
        public bool TryGetVisibleRowRange(out int firstRowIndex, out int rowCount)
        {
            EnsureUiThread();
            firstRowIndex = 0;
            rowCount = 0;
            if (!IsHandleCreated || Rows.Count == 0)
            {
                return false;
            }

            int first = FirstDisplayedScrollingRowIndex;
            if (first < 0)
            {
                if (FirstDisplayedCell == null)
                {
                    return false;
                }
                first = FirstDisplayedCell.RowIndex;
            }

            int displayed = DisplayedRowCount(true);
            if (displayed <= 0)
            {
                return false;
            }

            first = Math.Max(0, first - 1);
            int last = Math.Min(Rows.Count - 1, first + displayed + 1);
            firstRowIndex = first;
            rowCount = last - first + 1;
            return true;
        }

        /// <summary>判断指定显示行当前是否在可见范围内（UI 线程）。行号是 DataGridView 显示索引，排序后不等于数据行索引。</summary>
        /// <param name="rowIndex">显示行索引（DataGridView.Rows 的索引）。</param>
        /// <returns>行在可见范围内返回 <see langword="true"/>。</returns>
        public bool IsRowVisible(int rowIndex)
        {
            int first;
            int count;
            return TryGetVisibleRowRange(out first, out count)
                && rowIndex >= first
                && rowIndex < first + count;
        }

        /// <summary>
        /// 取当前可见行对应的 <see cref="DataTable"/> 数据行索引（UI 线程）。
        /// 与 <see cref="TryGetVisibleRowRange"/> 的显示行索引不同：控件启用列排序后，显示顺序与数据顺序
        /// 不再一致；需要按数据行号构造 <see cref="CellUpdate"/> 的调用方应使用本方法。
        /// </summary>
        /// <returns>当前可见区域（含上下各 1 行余量）对应的数据行索引列表。</returns>
        public IReadOnlyList<int> GetVisibleDataRowIndexes()
        {
            EnsureUiThread();
            var indexes = new List<int>();
            if (!IsHandleCreated || Rows.Count == 0)
            {
                return indexes;
            }

            int first = FirstDisplayedScrollingRowIndex;
            if (first < 0)
            {
                if (FirstDisplayedCell == null)
                {
                    return indexes;
                }
                first = FirstDisplayedCell.RowIndex;
            }

            int displayed = DisplayedRowCount(true);
            if (displayed <= 0)
            {
                return indexes;
            }

            first = Math.Max(0, first - 1);
            int last = Math.Min(Rows.Count - 1, first + displayed + 1);
            var seen = new HashSet<int>();
            for (int displayIndex = first; displayIndex <= last; displayIndex++)
            {
                DataGridViewRow row = Rows[displayIndex];
                DataRowView view = row.DataBoundItem as DataRowView;
                if (view != null)
                {
                    int dataIndex = dataTable.Rows.IndexOf(view.Row);
                    if (dataIndex >= 0 && seen.Add(dataIndex))
                    {
                        indexes.Add(dataIndex);
                    }
                }
                else if (displayIndex < rowItems.Count && seen.Add(displayIndex))
                {
                    indexes.Add(displayIndex);
                }
            }
            return indexes;
        }

        /// <summary>
        /// 写入一个单元格（UI 线程）。值无法转换为列类型时抛 <see cref="ArgumentException"/>。
        /// </summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <param name="columnIndex">列索引。</param>
        /// <param name="value">新值；<see langword="null"/> 按 <see cref="DBNull"/> 处理。</param>
        /// <exception cref="ArgumentOutOfRangeException">行或列索引越界。</exception>
        /// <exception cref="ArgumentException">值与列类型不兼容。</exception>
        /// <exception cref="InvalidOperationException">在非 UI 线程调用。</exception>
        public void SetCell(int rowIndex, int columnIndex, object value)
        {
            EnsureUiThread();
            ValidateRowIndex(rowIndex);
            ValidateColumnIndex(columnIndex);
            if (!TrySetCellCore(rowIndex, columnIndex, value))
            {
                throw new ArgumentException(LastCellUpdateError, nameof(value));
            }
        }

        /// <summary>
        /// 按列键写入一个单元格（UI 线程）。列键不存在或值不兼容时抛 <see cref="ArgumentException"/>。
        /// </summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <param name="columnKey">列键。</param>
        /// <param name="value">新值。</param>
        /// <exception cref="ArgumentException">列键不存在或值与列类型不兼容。</exception>
        public void SetCell(int rowIndex, string columnKey, object value)
        {
            EnsureUiThread();
            int columnIndex = GetColumnIndex(columnKey);
            if (columnIndex < 0)
            {
                throw new ArgumentException("列键不存在: " + columnKey, nameof(columnKey));
            }
            SetCell(rowIndex, columnIndex, value);
        }

        /// <summary>
        /// 尝试写入一个单元格（UI 线程）。索引越界或值不兼容时返回 <see langword="false"/>，不抛异常。
        /// </summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <param name="columnIndex">列索引。</param>
        /// <param name="value">新值。</param>
        /// <returns>写入成功返回 <see langword="true"/>。</returns>
        public bool TrySetCell(int rowIndex, int columnIndex, object value)
        {
            EnsureUiThread();
            if (!IsValidCell(rowIndex, columnIndex))
            {
                RecordCellError("单元格索引越界: 行 " + rowIndex + ", 列 " + columnIndex);
                return false;
            }
            return TrySetCellCore(rowIndex, columnIndex, value);
        }

        /// <summary>
        /// 按列键尝试写入一个单元格（UI 线程）。列键不存在时返回 <see langword="false"/>。
        /// </summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <param name="columnKey">列键。</param>
        /// <param name="value">新值。</param>
        /// <returns>写入成功返回 <see langword="true"/>。</returns>
        public bool TrySetCell(int rowIndex, string columnKey, object value)
        {
            EnsureUiThread();
            int columnIndex = GetColumnIndex(columnKey);
            if (columnIndex < 0)
            {
                RecordCellError("列键不存在: " + columnKey);
                return false;
            }
            return TrySetCell(rowIndex, columnIndex, value);
        }

        /// <summary>
        /// 批量写入单元格（UI 线程）。越界或类型不兼容的项会被跳过并计数，不中断整批。
        /// </summary>
        /// <param name="updates">要应用的更新；为 <see langword="null"/> 时直接返回 0。</param>
        /// <returns>成功写入的单元格数。</returns>
        /// <exception cref="InvalidOperationException">在非 UI 线程调用。</exception>
        public int ApplyUpdates(IReadOnlyList<CellUpdate> updates)
        {
            EnsureUiThread();
            if (updates == null)
            {
                return 0;
            }

            int applied = 0;
            int failed = 0;
            for (int i = 0; i < updates.Count; i++)
            {
                CellUpdate update = updates[i];
                if (!IsValidCell(update.RowIndex, update.ColumnIndex))
                {
                    RecordCellError("单元格索引越界: 行 " + update.RowIndex + ", 列 " + update.ColumnIndex);
                    failed++;
                    continue;
                }
                if (TrySetCellCore(update.RowIndex, update.ColumnIndex, update.Value))
                {
                    applied++;
                }
                else
                {
                    failed++;
                }
            }

            if (failed > 0)
            {
                LogCellErrors(failed);
            }
            return applied;
        }

        /// <summary>
        /// 线程安全的单单元格更新入口：任意线程可调用，更新入队后由 UI 线程应用。
        /// </summary>
        /// <param name="update">单元格更新。</param>
        /// <remarks>队列上限约 10 万条；满时丢弃并计入 <see cref="DroppedUpdateCount"/>。</remarks>
        public void PostCellUpdate(CellUpdate update)
        {
            if (disposed || Disposing)
            {
                return;
            }

            if (Interlocked.Increment(ref pendingUpdateCount) > MaxPendingUpdates)
            {
                Interlocked.Decrement(ref pendingUpdateCount);
                Interlocked.Increment(ref droppedUpdateCount);
                return;
            }

            pendingUpdates.Enqueue(update);
            ScheduleDrain();
        }

        /// <summary>
        /// 线程安全的批量更新入口：任意线程可调用；内部逐条入队并只投递一次 UI 回调。
        /// </summary>
        /// <param name="updates">要应用的更新集合；为 <see langword="null"/> 时什么都不做。</param>
        public void PostUpdates(IEnumerable<CellUpdate> updates)
        {
            if (updates == null)
            {
                return;
            }

            foreach (CellUpdate update in updates)
            {
                PostCellUpdate(update);
            }
        }

        /// <summary>按列键显示一列（UI 线程）。</summary>
        /// <param name="columnKey">列键。</param>
        /// <returns>找到并设置成功返回 <see langword="true"/>。</returns>
        public bool ShowColumn(string columnKey)
        {
            EnsureUiThread();
            DataGridViewColumn column = FindColumnByKey(columnKey);
            if (column == null)
            {
                return false;
            }
            column.Visible = true;
            return true;
        }

        /// <summary>
        /// 按列键隐藏一列（UI 线程）。第一列是键列，始终可见，隐藏第一列会返回 <see langword="false"/>。
        /// </summary>
        /// <param name="columnKey">列键。</param>
        /// <returns>找到并设置成功返回 <see langword="true"/>。</returns>
        public bool HideColumn(string columnKey)
        {
            EnsureUiThread();
            DataGridViewColumn column = FindColumnByKey(columnKey);
            if (column == null)
            {
                return false;
            }
            if (IsColumnLocked(columnKey))
            {
                return false;
            }
            column.Visible = false;
            return true;
        }

        /// <summary>
        /// 查询列是否可见（UI 线程）。列不存在时也返回 <see langword="false"/>，
        /// 需要区分“不存在”与“隐藏”时请先用 <see cref="GetColumnIndex"/> 判断。
        /// </summary>
        /// <param name="columnKey">列键。</param>
        /// <returns>列存在且可见返回 <see langword="true"/>。</returns>
        public bool ShownInColumns(string columnKey)
        {
            EnsureUiThread();
            DataGridViewColumn column = FindColumnByKey(columnKey);
            return column != null && column.Visible;
        }

        /// <summary>
        /// 显示全部列并写回布局文件（未设置 <see cref="LayoutFile"/> 时只改界面）。
        /// </summary>
        public void ShowAllColumns()
        {
            EnsureUiThread();
            ShowAllColumnsCore();
            if (!string.IsNullOrEmpty(LayoutFile))
            {
                SaveLayout();
            }
        }

        /// <summary>
        /// 恢复默认布局（全部显示、列宽回到 <see cref="ColumnStruct.width"/>），并写回布局文件。
        /// </summary>
        /// <returns>布局文件写入成功返回 <see langword="true"/>；未设置 <see cref="LayoutFile"/> 或写入失败返回 <see langword="false"/>。</returns>
        public bool ResetLayout()
        {
            EnsureUiThread();
            ApplyDefaults();
            return SaveLayout();
        }

        /// <summary>
        /// 从 <see cref="LayoutFile"/> 读取布局并应用：按列键恢复列宽与显隐，并按文件中的列顺序恢复显示顺序。
        /// </summary>
        /// <returns>
        /// 成功应用返回 <see langword="true"/>；未设置文件名、文件不存在、JSON 损坏或版本不支持返回
        /// <see langword="false"/>，并把布局回退为“全部显示 + 默认列宽 + 定义顺序”。
        /// </returns>
        /// <remarks>前 <see cref="LockedColumnCount"/> 列始终强制可见；文件中缺失的列按默认宽度显示并排在已知列之后。</remarks>
        public bool LoadLayout()
        {
            EnsureUiThread();
            LastLayoutError = null;
            if (string.IsNullOrEmpty(LayoutFile))
            {
                return false;
            }

            try
            {
                if (!File.Exists(LayoutFile))
                {
                    ApplyDefaults();
                    return false;
                }

                string json = File.ReadAllText(LayoutFile);
                LayoutFileModel model = JsonConvert.DeserializeObject<LayoutFileModel>(json);
                if (model == null || model.Version <= 0 || model.Version > LayoutFileVersion)
                {
                    LastLayoutError = "布局文件版本无效（" + (model == null ? "null" : model.Version.ToString()) + "），已回退为全部显示。";
                    ApplyDefaults();
                    return false;
                }

                ApplyLayoutModel(model);
                loadedLayout = model;
                layoutWidthsChangedByUser = false;
                return true;
            }
            catch (Exception ex)
            {
                LastLayoutError = "读取布局失败(" + LayoutFile + "): " + ex.Message;
                Log.WriteLog(LastLayoutError, "Layout", "IndustrialDataGridView");
                ApplyDefaults();
                return false;
            }
        }

        /// <summary>
        /// 把当前的列宽、显隐与显示顺序写入 <see cref="LayoutFile"/>（版本 2；列宽为 96 DPI 逻辑像素，
        /// 加载时按 DeviceDpi/96 缩放；原子写入：临时文件 + 替换 + .bak 备份）。
        /// </summary>
        /// <returns>写入成功返回 <see langword="true"/>；未设置文件名、没有列或写入失败返回 <see langword="false"/>。</returns>
        public bool SaveLayout()
        {
            EnsureUiThread();
            LastLayoutError = null;
            if (string.IsNullOrEmpty(LayoutFile) || Columns.Count == 0)
            {
                return false;
            }

            savingLayout = true;
            try
            {
                var model = new LayoutFileModel
                {
                    Version = LayoutFileVersion,
                    Dpi = LogicalDpi,
                    Columns = new List<LayoutColumnModel>(Columns.Count)
                };
                foreach (DataGridViewColumn column in GetColumnsInDisplayOrder())
                {
                    model.Columns.Add(new LayoutColumnModel
                    {
                        Key = column.Name,
                        Visible = column.Visible,
                        Width = ToLogicalWidth(GetBaseWidth(column))
                    });
                }

                string json = JsonConvert.SerializeObject(model, Formatting.Indented);
                WriteAllTextAtomic(LayoutFile, json);
                columnWidthSavePending = false;
                loadedLayout = model;
                loadedLayoutDpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
                widthsDpi = loadedLayoutDpi;
                layoutWidthsChangedByUser = false;
                return true;
            }
            catch (Exception ex)
            {
                LastLayoutError = "保存布局失败(" + LayoutFile + "): " + ex.Message;
                Log.WriteLog(LastLayoutError, "Layout", "IndustrialDataGridView");
                return false;
            }
            finally
            {
                savingLayout = false;
            }
        }

        /// <summary>
        /// 迁移旧版的“列宽 JSON（ID/width）+ 显隐 CSV（1/0）”到新的单文件布局。
        /// </summary>
        /// <param name="legacyWidthFile">旧列宽 JSON 路径；为空或不存在时跳过列宽。</param>
        /// <param name="legacyShownFile">旧显隐 CSV 路径；为空或不存在时跳过显隐。</param>
        /// <returns>迁移并写入新布局成功返回 <see langword="true"/>；新布局已存在、旧文件不存在或读写失败返回 <see langword="false"/>。</returns>
        /// <remarks>
        /// 旧格式按“当前列顺序”映射（旧列宽是保存时 DPI 下的物理像素）；迁移后写为新版 96 DPI 逻辑宽度，
        /// 成功后旧文件改名为 <c>*.bak</c>。
        /// 第一列始终强制可见。
        /// </remarks>
        public bool MigrateLegacyLayout(string legacyWidthFile, string legacyShownFile)
        {
            EnsureUiThread();
            LastLayoutError = null;
            if (string.IsNullOrEmpty(LayoutFile))
            {
                LastLayoutError = "未设置 LayoutFile，无法迁移。";
                return false;
            }
            if (File.Exists(LayoutFile))
            {
                LastLayoutError = "新布局文件已存在，未执行迁移: " + LayoutFile;
                return false;
            }

            bool hasWidth = !string.IsNullOrEmpty(legacyWidthFile) && File.Exists(legacyWidthFile);
            bool hasShown = !string.IsNullOrEmpty(legacyShownFile) && File.Exists(legacyShownFile);
            if (!hasWidth && !hasShown)
            {
                LastLayoutError = "旧布局文件不存在，未执行迁移。";
                return false;
            }

            loadingLayout = true;
            try
            {
                ApplyDefaultWidths();
                ShowAllColumnsCore();

                if (hasWidth)
                {
                    string json = File.ReadAllText(legacyWidthFile);
                    List<LegacyColumnWidthEntry> widths = JsonConvert.DeserializeObject<List<LegacyColumnWidthEntry>>(json);
                    if (widths != null)
                    {
                        foreach (LegacyColumnWidthEntry entry in widths)
                        {
                            if (entry == null || entry.ID < 0 || entry.ID >= Columns.Count || entry.Width <= 0)
                            {
                                continue;
                            }

                            int width = Math.Min(entry.Width, 65535);
                            if (width < Columns[entry.ID].MinimumWidth)
                            {
                                width = Columns[entry.ID].MinimumWidth;
                            }
                            Columns[entry.ID].Width = width;
                        }
                    }
                }

                if (hasShown)
                {
                    string content = File.ReadAllText(legacyShownFile);
                    string[] parts = content.Split(',');
                    for (int i = 0; i < parts.Length && i < Columns.Count; i++)
                    {
                        if (parts[i].Trim() == "0")
                        {
                            Columns[i].Visible = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastLayoutError = "迁移旧布局失败: " + ex.Message;
                Log.WriteLog(LastLayoutError, "Layout", "IndustrialDataGridView");
                return false;
            }
            finally
            {
                loadingLayout = false;
            }

            foreach (DataGridViewColumn column in Columns)
            {
                SetBaseWidth(column, column.Width);
            }
            AutoFitColumnHeaders();
            ForceFirstColumnVisible();
            if (!SaveLayout())
            {
                if (string.IsNullOrEmpty(LastLayoutError))
                {
                    LastLayoutError = "迁移后写入新布局失败。";
                }
                return false;
            }

            BackupLegacyFile(legacyWidthFile, hasWidth);
            BackupLegacyFile(legacyShownFile, hasShown);
            return true;
        }

        /// <summary>把字号放大 0.5 磅（上限 <see cref="MaxFontSize"/>），并同步刷新可见行高与行模板。</summary>
        public void IncreaseFont()
        {
            EnsureUiThread();
            float current = Font.SizeInPoints;
            if (current >= MaxFontSize)
            {
                return;
            }

            float next = Math.Min(MaxFontSize, current + 0.5f);
            ApplyFont(new Font(Font.Name, next, FontStyle.Regular, GraphicsUnit.Point));
        }

        /// <summary>把字号缩小 0.5 磅（下限 <see cref="MinFontSize"/>），并同步刷新可见行高与行模板。</summary>
        public void DecreaseFont()
        {
            EnsureUiThread();
            float current = Font.SizeInPoints;
            if (current <= MinFontSize)
            {
                return;
            }

            float next = Math.Max(MinFontSize, current - 0.5f);
            ApplyFont(new Font(Font.Name, next, FontStyle.Regular, GraphicsUnit.Point));
        }

        /// <summary>恢复默认字号（<see cref="DefaultFontSize"/>，常规字形），并同步刷新可见行高与行模板。</summary>
        public void ResetFontSize()
        {
            EnsureUiThread();
            ApplyFont(new Font("微软雅黑", DefaultFontSize, FontStyle.Regular, GraphicsUnit.Point));
        }

        /// <summary>按当前字体与 DPI 重新计算行模板与当前可见行；不可见行在滚动进入可见区时补齐。</summary>
        public void ChangeRowHeight()
        {
            EnsureUiThread();
            UpdateRowHeights();
        }

        /// <summary>
        /// 键盘快捷键：<c>Ctrl+↑</c> 放大字号、<c>Ctrl+↓</c> 缩小字号。
        /// </summary>
        /// <param name="e">按键事件参数。</param>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Up)
            {
                IncreaseFont();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.Down)
            {
                DecreaseFont();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            base.OnKeyDown(e);
        }

        /// <summary>字体变化时重新计算行高，并按新字体自动加宽列以完整显示列头。</summary>
        /// <param name="e">事件参数。</param>
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateRowHeights();
            AutoFitColumnHeaders();
        }

        /// <summary>DPI 变化时重新计算行高；若布局已加载且用户未改过列宽，则按新 DPI 重新套用列宽。</summary>
        /// <param name="e">事件参数。</param>
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateRowHeights();
            if (loadedLayout != null && !layoutWidthsChangedByUser && loadedLayout.Dpi > 0)
            {
                ReapplyLoadedLayoutIfDpiChanged();
            }
            else
            {
                ScaleColumnWidthsForDpi();
            }
        }

        /// <summary>创建句柄后按当前 DPI 重算行高；若布局是在句柄创建前读取的，则按实际 DPI 重新套用列宽。</summary>
        /// <param name="e">事件参数。</param>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRowHeights();
            if (loadedLayout != null && !layoutWidthsChangedByUser && loadedLayout.Dpi > 0)
            {
                ReapplyLoadedLayoutIfDpiChanged();
            }
            else
            {
                ScaleColumnWidthsForDpi();
            }
            if (Volatile.Read(ref pendingUpdateCount) > 0)
            {
                ScheduleDrain();
            }
        }

        /// <summary>句柄销毁时清除排队标记，避免残留的派发状态。</summary>
        /// <param name="e">事件参数。</param>
        protected override void OnHandleDestroyed(EventArgs e)
        {
            Interlocked.Exchange(ref drainScheduled, 0);
            base.OnHandleDestroyed(e);
        }

        /// <summary>
        /// 释放资源：停止布局去抖定时器并补写未落盘的布局，丢弃待应用的队列，最后释放本控件自己创建的字体。
        /// </summary>
        /// <param name="disposing">为 <see langword="true"/> 时释放托管资源。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed = true;
                try
                {
                    columnWidthSaveTimer.Stop();
                    columnWidthSaveTimer.Dispose();
                }
                catch (Exception)
                {
                }

                if (columnWidthSavePending)
                {
                    try
                    {
                        SaveLayout();
                    }
                    catch (Exception)
                    {
                    }
                    columnWidthSavePending = false;
                }

                CellUpdate update;
                while (pendingUpdates.TryDequeue(out update))
                {
                }
                Interlocked.Exchange(ref pendingUpdateCount, 0);
            }

            base.Dispose(disposing);

            if (disposing)
            {
                foreach (Font font in ownedFonts)
                {
                    try
                    {
                        font.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
                ownedFonts.Clear();
                appliedFont = null;
            }
        }

        private static DataTable CreateDataTable(ColumnStruct[] definitions)
        {
            var table = new DataTable("IndustrialDataTable");
            foreach (ColumnStruct definition in definitions)
            {
                try
                {
                    table.Columns.Add(new DataColumn(definition.columnName, definition.columnType));
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        "列 " + definition.columnName + " 的类型 " + definition.columnType + " 不受 DataTable 支持: " + ex.Message,
                        ex);
                }
            }
            return table;
        }

        private void InitializeGrid(Color background, Color selectionColor)
        {
            AutoGenerateColumns = false;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            AllowUserToResizeColumns = true;
            AllowUserToOrderColumns = true;
            AllowDrop = false;
            ReadOnly = true;
            EditMode = DataGridViewEditMode.EditProgrammatically;
            BackgroundColor = background;
            BorderStyle = BorderStyle.None;
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            EnableHeadersVisualStyles = false;
            GridColor = Color.Silver;
            RowHeadersVisible = false;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            ShowCellToolTips = false;
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            DoubleBuffered = true;

            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = background,
                ForeColor = Color.White,
                SelectionBackColor = background,
                SelectionForeColor = Color.White,
                WrapMode = DataGridViewTriState.False
            };
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                BackColor = background,
                ForeColor = Color.White,
                SelectionBackColor = selectionColor,
                SelectionForeColor = SystemColors.HighlightText,
                WrapMode = DataGridViewTriState.False
            };

            var initialFont = new Font("微软雅黑", DefaultFontSize, FontStyle.Regular, GraphicsUnit.Point);
            ownedFonts.Add(initialFont);
            appliedFont = initialFont;
            Font = initialFont;
        }

        private void CreateColumns()
        {
            foreach (ColumnStruct definition in columnDefinitions)
            {
                DataGridViewColumn column;
                if (definition.columnType == typeof(bool))
                {
                    column = new DataGridViewCheckBoxColumn();
                }
                else
                {
                    column = new DataGridViewTextBoxColumn();
                }

                column.Name = definition.columnName;
                column.DataPropertyName = definition.columnName;
                column.HeaderText = string.IsNullOrWhiteSpace(definition.headerText)
                    ? definition.columnName
                    : definition.headerText;
                column.SortMode = definition.sortable
                    ? DataGridViewColumnSortMode.Automatic
                    : DataGridViewColumnSortMode.NotSortable;
                column.ReadOnly = true;
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Visible = definition.visible ?? true;

                int currentDpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
                int logicalMinimum = definition.minimumWidth > 0 ? definition.minimumWidth : DefaultMinimumColumnWidth;
                int logicalWidth = definition.width > 0 ? definition.width : DefaultColumnWidth;
                int minimumWidth = ScaleWidth(logicalMinimum, LogicalDpi, currentDpi, 1);
                int width = ScaleWidth(logicalWidth, LogicalDpi, currentDpi, minimumWidth);
                column.MinimumWidth = Math.Min(minimumWidth, 65535);
                column.Width = Math.Min(width, 65535);
                SetBaseWidth(column, column.Width);

                if (!string.IsNullOrEmpty(definition.format) && definition.columnType != typeof(bool))
                {
                    column.DefaultCellStyle.Format = definition.format;
                }
                column.DefaultCellStyle.Alignment = ResolveAlignment(definition);

                Columns.Add(column);
            }
            widthsDpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
        }

        private static DataGridViewContentAlignment ResolveAlignment(ColumnStruct definition)
        {
            if (definition.alignment != DataGridViewContentAlignment.NotSet)
            {
                return definition.alignment;
            }
            if (definition.columnType == typeof(bool))
            {
                return DataGridViewContentAlignment.MiddleCenter;
            }
            return IsNumericType(definition.columnType)
                ? DataGridViewContentAlignment.MiddleRight
                : DataGridViewContentAlignment.MiddleLeft;
        }

        private static bool IsNumericType(Type type)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    return true;
                default:
                    return false;
            }
        }

        private object[] GetRowValuesChecked(T item, int rowIndex)
        {
            object[] values = GetRowValues(item);
            if (values == null)
            {
                throw new InvalidOperationException("GetRowValues 返回 null（第 " + rowIndex + " 行）。");
            }
            if (values.Length != columnDefinitions.Length)
            {
                throw new InvalidOperationException(
                    "GetRowValues 返回 " + values.Length + " 个值，但控件有 " + columnDefinitions.Length + " 列（第 " + rowIndex + " 行）。");
            }

            var copy = new object[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }

        private bool IsValidCell(int rowIndex, int columnIndex)
        {
            return rowIndex >= 0 && rowIndex < dataTable.Rows.Count
                && columnIndex >= 0 && columnIndex < dataTable.Columns.Count;
        }

        private void ValidateRowIndex(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= dataTable.Rows.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rowIndex),
                    "行索引超出范围: " + rowIndex + "（当前行数 " + dataTable.Rows.Count + "）。");
            }
        }

        private void ValidateColumnIndex(int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= dataTable.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columnIndex),
                    "列索引超出范围: " + columnIndex + "（当前列数 " + dataTable.Columns.Count + "）。");
            }
        }

        private bool TrySetCellCore(int rowIndex, int columnIndex, object value)
        {
            try
            {
                object oldAlarmValue = columnIndex == alarmColumnIndex
                    ? dataTable.Rows[rowIndex][columnIndex]
                    : null;
                dataTable.Rows[rowIndex][columnIndex] = value;
                if (columnIndex == alarmColumnIndex)
                {
                    object newAlarmValue = dataTable.Rows[rowIndex][columnIndex];
                    if (!AlarmValuesEqual(oldAlarmValue, newAlarmValue))
                    {
                        Invalidate();
                    }
                }
                return true;
            }
            catch (Exception ex) when (IsCellValueException(ex))
            {
                RecordCellError(
                    "行 " + rowIndex + " 列 " + columnIndex + " 写入 " + DescribeValue(value) + " 失败: " + ex.Message);
                return false;
            }
        }

        private static bool AlarmValuesEqual(object oldValue, object newValue)
        {
            if (oldValue == null || oldValue == DBNull.Value)
            {
                oldValue = null;
            }
            if (newValue == null || newValue == DBNull.Value)
            {
                newValue = null;
            }
            return object.Equals(oldValue, newValue);
        }

        private static bool IsCellValueException(Exception ex)
        {
            return ex is ArgumentException
                || ex is FormatException
                || ex is InvalidCastException
                || ex is OverflowException
                || ex is ConstraintException
                || ex is NoNullAllowedException
                || ex is DeletedRowInaccessibleException;
        }

        private static string DescribeValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            string text = value.ToString();
            if (text != null && text.Length > 64)
            {
                text = text.Substring(0, 64) + "...";
            }
            return text;
        }

        private void RecordCellError(string message)
        {
            LastCellUpdateError = message;
            Interlocked.Increment(ref failedCellUpdateCount);
        }

        private void LogCellErrors(int failed)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - lastCellErrorLogTicks < CellErrorLogIntervalTicks)
            {
                return;
            }

            lastCellErrorLogTicks = now;
            Log.WriteLog(
                "批量更新失败 " + failed + " 个单元格；最近错误: " + LastCellUpdateError,
                "Cell",
                "IndustrialDataGridView");
        }

        private void ScheduleDrain()
        {
            if (disposed || !IsHandleCreated)
            {
                return;
            }
            if (Interlocked.Exchange(ref drainScheduled, 1) != 0)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(DrainPending));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref drainScheduled, 0);
            }
        }

        private void DrainPending()
        {
            Interlocked.Exchange(ref drainScheduled, 0);
            if (disposed || Disposing)
            {
                return;
            }

            int failed = 0;
            CellUpdate update;
            while (pendingUpdates.TryDequeue(out update))
            {
                Interlocked.Decrement(ref pendingUpdateCount);
                if (!IsValidCell(update.RowIndex, update.ColumnIndex))
                {
                    RecordCellError("单元格索引越界: 行 " + update.RowIndex + ", 列 " + update.ColumnIndex);
                    failed++;
                    continue;
                }
                if (!TrySetCellCore(update.RowIndex, update.ColumnIndex, update.Value))
                {
                    failed++;
                }
            }

            if (failed > 0)
            {
                LogCellErrors(failed);
            }
            if (Volatile.Read(ref pendingUpdateCount) > 0)
            {
                ScheduleDrain();
            }
        }

        private void ShowAllColumnsCore()
        {
            foreach (DataGridViewColumn column in Columns)
            {
                column.Visible = true;
            }
            ForceFirstColumnVisible();
        }

        private void ForceFirstColumnVisible()
        {
            foreach (DataGridViewColumn column in lockedColumns)
            {
                if (!column.Visible)
                {
                    column.Visible = true;
                }
            }
        }

        private void ResetColumnOrderToDefinitions()
        {
            var ordered = new List<DataGridViewColumn>(Columns.Count);
            var seen = new HashSet<DataGridViewColumn>();
            foreach (ColumnStruct definition in columnDefinitions)
            {
                DataGridViewColumn column = FindColumnByKey(definition.columnName);
                if (column != null && seen.Add(column))
                {
                    ordered.Add(column);
                }
            }
            foreach (DataGridViewColumn column in GetColumnsInDisplayOrder())
            {
                if (seen.Add(column))
                {
                    ordered.Add(column);
                }
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].DisplayIndex != i)
                {
                    ordered[i].DisplayIndex = i;
                }
            }
        }

        private void ApplyDefaults()
        {
            loadingLayout = true;
            try
            {
                ApplyDefaultWidths();
                ShowAllColumnsCore();
                ResetColumnOrderToDefinitions();
                InitializeLockedColumns();
                ApplyLockedColumnState();
                AutoFitColumnHeaders();
            }
            finally
            {
                loadingLayout = false;
                loadedLayout = null;
                loadedLayoutDpi = 0;
                layoutWidthsChangedByUser = false;
            }
        }

        private void ApplyDefaultWidths()
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
            for (int i = 0; i < Columns.Count; i++)
            {
                int index = GetColumnIndex(Columns[i].Name);
                int logicalMinimum = DefaultMinimumColumnWidth;
                int logicalWidth = DefaultColumnWidth;
                if (index >= 0 && index < columnDefinitions.Length)
                {
                    ColumnStruct definition = columnDefinitions[index];
                    logicalMinimum = definition.minimumWidth > 0 ? definition.minimumWidth : DefaultMinimumColumnWidth;
                    logicalWidth = definition.width > 0 ? definition.width : DefaultColumnWidth;
                }

                int minimum = ScaleWidth(logicalMinimum, LogicalDpi, dpi, 1);
                int width = ScaleWidth(logicalWidth, LogicalDpi, dpi, minimum);
                Columns[i].MinimumWidth = Math.Min(minimum, 65535);
                Columns[i].Width = Math.Min(width, 65535);
                SetBaseWidth(Columns[i], Columns[i].Width);
                Columns[i].Visible = true;
            }
            widthsDpi = dpi;
            ForceFirstColumnVisible();
            AutoFitColumnHeaders();
        }

        private void ApplyLayoutModel(LayoutFileModel model)
        {
            loadingLayout = true;
            try
            {
                var layoutByKey = new Dictionary<string, LayoutColumnModel>(StringComparer.OrdinalIgnoreCase);
                if (model.Columns != null)
                {
                    foreach (LayoutColumnModel layout in model.Columns)
                    {
                        if (layout != null && !string.IsNullOrEmpty(layout.Key))
                        {
                            layoutByKey[layout.Key] = layout;
                        }
                    }
                }

                int currentDpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
                // v2 存的是 96 DPI 逻辑宽度；v1 存的是保存时 DPI 下的物理宽度，按旧规则缩放。
                int sourceDpi = model.Version >= 2
                    ? LogicalDpi
                    : (model.Dpi > 0 ? model.Dpi : currentDpi);
                foreach (DataGridViewColumn column in Columns)
                {
                    LayoutColumnModel layout;
                    if (layoutByKey.TryGetValue(column.Name, out layout))
                    {
                        if (layout.Width > 0)
                        {
                            column.Width = ScaleWidth(layout.Width, sourceDpi, currentDpi, column.MinimumWidth);
                        }
                        SetBaseWidth(column, column.Width);
                        column.Visible = layout.Visible;
                    }
                    else
                    {
                        column.Visible = true;
                        int defaultWidth = GetDefaultColumnWidth(column.Name);
                        column.Width = defaultWidth;
                        SetBaseWidth(column, defaultWidth);
                    }
                }

                var ordered = new List<DataGridViewColumn>(Columns.Count);
                var seen = new HashSet<DataGridViewColumn>();
                if (model.Columns != null)
                {
                    foreach (LayoutColumnModel layout in model.Columns)
                    {
                        if (layout == null || string.IsNullOrEmpty(layout.Key))
                        {
                            continue;
                        }
                        DataGridViewColumn column = FindColumnByKey(layout.Key);
                        if (column != null && seen.Add(column))
                        {
                            ordered.Add(column);
                        }
                    }
                }
                foreach (DataGridViewColumn column in GetColumnsInDisplayOrder())
                {
                    if (seen.Add(column))
                    {
                        ordered.Add(column);
                    }
                }
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].DisplayIndex != i)
                    {
                        ordered[i].DisplayIndex = i;
                    }
                }

                InitializeLockedColumns();
                ApplyLockedColumnState();
                AutoFitColumnHeaders();
            }
            finally
            {
                loadingLayout = false;
                loadedLayoutDpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
                widthsDpi = loadedLayoutDpi;
                layoutWidthsChangedByUser = false;
            }
        }

        private void ReapplyLoadedLayoutIfDpiChanged()
        {
            if (loadedLayout == null || layoutWidthsChangedByUser || loadedLayout.Dpi <= 0)
            {
                return;
            }

            int currentDpi = DeviceDpi > 0 ? DeviceDpi : 96;
            if (currentDpi == loadedLayoutDpi)
            {
                return;
            }

            try
            {
                ApplyLayoutModel(loadedLayout);
            }
            catch (Exception ex)
            {
                LastLayoutError = "重新应用布局失败: " + ex.Message;
                Log.WriteLog(LastLayoutError, "Layout", "IndustrialDataGridView");
            }
        }

        private int GetDefaultColumnWidth(string columnKey)
        {
            int index = GetColumnIndex(columnKey);
            int logicalMinimum = DefaultMinimumColumnWidth;
            int logicalWidth = DefaultColumnWidth;
            if (index >= 0 && index < columnDefinitions.Length)
            {
                ColumnStruct definition = columnDefinitions[index];
                logicalMinimum = definition.minimumWidth > 0 ? definition.minimumWidth : DefaultMinimumColumnWidth;
                logicalWidth = definition.width > 0 ? definition.width : DefaultColumnWidth;
            }

            int dpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
            int minimum = ScaleWidth(logicalMinimum, LogicalDpi, dpi, 1);
            return ScaleWidth(logicalWidth, LogicalDpi, dpi, minimum);
        }

        /// <summary>
        /// 按当前字体把每个列宽调整到至少能完整显示列头文字（不会小于用户/布局设置的基准宽度）。
        /// 字体变化时自动调用；列头文字运行期改变后也可手动调用。
        /// </summary>
        public void AutoFitColumnHeaders()
        {
            EnsureUiThread();
            if (Columns.Count == 0)
            {
                return;
            }

            bool oldAutoFitting = autoFittingWidths;
            autoFittingWidths = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    int target = Math.Max(GetBaseWidth(column), MeasureHeaderWidth(column));
                    target = Math.Max(target, column.MinimumWidth);
                    target = Math.Min(target, 65535);
                    if (column.Width != target)
                    {
                        column.Width = target;
                    }
                }
            }
            finally
            {
                autoFittingWidths = oldAutoFitting;
            }
        }

        private int MeasureHeaderWidth(DataGridViewColumn column)
        {
            string text = string.IsNullOrEmpty(column.HeaderText) ? column.Name : column.HeaderText;
            Font font = column.HeaderCell.InheritedStyle.Font;
            if (font == null)
            {
                font = Font;
            }

            Size size = TextRenderer.MeasureText(
                text,
                font,
                new Size(10000, 10000),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int padding = 18;
            if (column.SortMode == DataGridViewColumnSortMode.Automatic)
            {
                padding += 18;
            }
            return Math.Min(65535, Math.Max(1, size.Width + padding));
        }

        private int GetBaseWidth(DataGridViewColumn column)
        {
            int width;
            if (baseColumnWidths.TryGetValue(column.Name, out width) && width > 0)
            {
                return width;
            }
            return Math.Max(1, column.Width);
        }

        private void SetBaseWidth(DataGridViewColumn column, int width)
        {
            baseColumnWidths[column.Name] = Math.Max(1, Math.Min(width, 65535));
        }

        private static int ScaleWidth(int width, int sourceDpi, int targetDpi, int minimumWidth)
        {
            if (width <= 0)
            {
                return 0;
            }
            if (sourceDpi <= 0)
            {
                sourceDpi = 96;
            }
            if (targetDpi <= 0)
            {
                targetDpi = 96;
            }

            int scaled = sourceDpi == targetDpi
                ? width
                : (int)Math.Round(width * (double)targetDpi / sourceDpi);
            if (scaled < minimumWidth)
            {
                scaled = minimumWidth;
            }
            if (scaled < 1)
            {
                scaled = 1;
            }
            if (scaled > 65535)
            {
                scaled = 65535;
            }
            return scaled;
        }

        private int ToLogicalWidth(int physicalWidth)
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
            if (dpi == LogicalDpi)
            {
                return Math.Min(Math.Max(physicalWidth, 1), 65535);
            }
            int logical = (int)Math.Round(physicalWidth * (double)LogicalDpi / dpi);
            return Math.Min(Math.Max(logical, 1), 65535);
        }

        /// <summary>
        /// 没有加载布局时（或用户已手工改过列宽），跨 DPI 按比例缩放当前物理列宽/最小列宽。
        /// 有布局时优先 <see cref="ApplyLayoutModel"/> 从 96 DPI 逻辑宽度重新计算。
        /// </summary>
        private void ScaleColumnWidthsForDpi()
        {
            int oldDpi = widthsDpi > 0 ? widthsDpi : LogicalDpi;
            int newDpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
            if (oldDpi == newDpi)
            {
                return;
            }

            loadingLayout = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    int newMinimum = ScaleWidth(column.MinimumWidth, oldDpi, newDpi, 1);
                    int newBase = ScaleWidth(GetBaseWidth(column), oldDpi, newDpi, newMinimum);
                    column.MinimumWidth = newMinimum;
                    SetBaseWidth(column, newBase);
                    column.Width = newBase;
                }
                widthsDpi = newDpi;
            }
            finally
            {
                loadingLayout = false;
            }
            AutoFitColumnHeaders();
        }

        private List<DataGridViewColumn> GetColumnsInDisplayOrder()
        {
            var ordered = new List<DataGridViewColumn>(Columns.Count);
            foreach (DataGridViewColumn column in Columns)
            {
                ordered.Add(column);
            }
            ordered.Sort((a, b) => a.DisplayIndex.CompareTo(b.DisplayIndex));
            return ordered;
        }

        private void InitializeLockedColumns()
        {
            lockedColumns.Clear();
            if (Columns.Count == 0)
            {
                return;
            }

            List<DataGridViewColumn> ordered = GetColumnsInDisplayOrder();
            int count = Math.Min(lockedColumnCount, ordered.Count);
            for (int i = 0; i < count; i++)
            {
                lockedColumns.Add(ordered[i]);
            }
        }

        /// <summary>
        /// 把锁定列固定到最左侧并设置 Frozen=true；未锁定列保持当前相对顺序。
        /// </summary>
        private void ApplyLockedColumns()
        {
            if (Columns.Count == 0)
            {
                return;
            }

            bool oldLoading = loadingLayout;
            enforcingLockedColumns = true;
            loadingLayout = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    column.Frozen = false;
                }

                List<DataGridViewColumn> ordered = GetColumnsInDisplayOrder();
                var unlocked = new List<DataGridViewColumn>();
                foreach (DataGridViewColumn column in ordered)
                {
                    if (!lockedColumns.Contains(column))
                    {
                        unlocked.Add(column);
                    }
                }

                for (int i = 0; i < lockedColumns.Count; i++)
                {
                    if (lockedColumns[i].DisplayIndex != i)
                    {
                        lockedColumns[i].DisplayIndex = i;
                    }
                }
                for (int i = 0; i < unlocked.Count; i++)
                {
                    int target = lockedColumns.Count + i;
                    if (unlocked[i].DisplayIndex != target)
                    {
                        unlocked[i].DisplayIndex = target;
                    }
                }

                foreach (DataGridViewColumn column in lockedColumns)
                {
                    column.Visible = true;
                    column.Frozen = true;
                }
            }
            finally
            {
                loadingLayout = oldLoading;
                enforcingLockedColumns = false;
            }
            ForceFirstColumnVisible();
        }

        private void ApplyLockedColumnState()
        {
            if (Columns.Count == 0)
            {
                return;
            }

            bool oldLoading = loadingLayout;
            enforcingLockedColumns = true;
            loadingLayout = true;
            try
            {
                foreach (DataGridViewColumn column in Columns)
                {
                    column.Frozen = false;
                }
                foreach (DataGridViewColumn column in lockedColumns)
                {
                    column.Visible = true;
                    column.Frozen = true;
                }
            }
            finally
            {
                loadingLayout = oldLoading;
                enforcingLockedColumns = false;
            }
            ForceFirstColumnVisible();
        }

        private void EnforceLockedColumnOrder()
        {
            if (lockedColumns.Count == 0)
            {
                ApplyLockedColumnState();
                return;
            }

            List<DataGridViewColumn> ordered = GetColumnsInDisplayOrder();
            bool valid = true;
            for (int i = 0; i < lockedColumns.Count; i++)
            {
                if (i >= ordered.Count || !ReferenceEquals(ordered[i], lockedColumns[i]))
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
            {
                ApplyLockedColumns();
            }
            else
            {
                ApplyLockedColumnState();
            }
        }

        private void ScheduleLayoutSave()
        {
            if (disposed || string.IsNullOrEmpty(LayoutFile))
            {
                return;
            }

            layoutWidthsChangedByUser = true;
            columnWidthSavePending = true;
            columnWidthSaveTimer.Stop();
            columnWidthSaveTimer.Start();
        }

        private void OnColumnDisplayIndexChangedInternal(object sender, DataGridViewColumnEventArgs e)
        {
            if (disposed || loadingLayout || enforcingLockedColumns)
            {
                return;
            }

            EnforceLockedColumnOrder();
            ScheduleLayoutSave();
        }

        private void OnColumnStateChangedInternal(object sender, DataGridViewColumnStateChangedEventArgs e)
        {
            if (disposed || loadingLayout || enforcingLockedColumns)
            {
                return;
            }
            if ((e.StateChanged & (DataGridViewElementStates.Visible | DataGridViewElementStates.Frozen))
                == 0)
            {
                return;
            }

            bool locked = false;
            foreach (DataGridViewColumn column in lockedColumns)
            {
                if (ReferenceEquals(column, e.Column))
                {
                    locked = true;
                    break;
                }
            }

            if (locked)
            {
                if (!e.Column.Visible || !e.Column.Frozen)
                {
                    ApplyLockedColumnState();
                }
            }
            else if (e.Column.Frozen)
            {
                enforcingLockedColumns = true;
                try
                {
                    e.Column.Frozen = false;
                }
                finally
                {
                    enforcingLockedColumns = false;
                }
            }
        }

        private void OnCellFormattingInternal(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || alarmColumnIndex < 0)
            {
                return;
            }

            DataGridViewRow row = Rows[e.RowIndex];
            DataRowView view = row.DataBoundItem as DataRowView;
            bool alarm;
            if (view != null)
            {
                alarm = IsAlarmRowCore(view.Row);
            }
            else
            {
                if (e.RowIndex >= dataTable.Rows.Count)
                {
                    return;
                }
                alarm = IsAlarmRowCore(dataTable.Rows[e.RowIndex]);
            }

            if (alarm)
            {
                e.CellStyle.BackColor = alarmRowBackColor;
                e.CellStyle.ForeColor = alarmRowForeColor;
                e.CellStyle.SelectionBackColor = alarmRowSelectedBackColor.IsEmpty
                    ? DefaultCellStyle.SelectionBackColor
                    : alarmRowSelectedBackColor;
                e.CellStyle.SelectionForeColor = alarmRowSelectedForeColor.IsEmpty
                    ? DefaultCellStyle.SelectionForeColor
                    : alarmRowSelectedForeColor;
            }
            else
            {
                e.CellStyle.BackColor = DefaultCellStyle.BackColor;
                e.CellStyle.ForeColor = DefaultCellStyle.ForeColor;
                e.CellStyle.SelectionBackColor = DefaultCellStyle.SelectionBackColor;
                e.CellStyle.SelectionForeColor = DefaultCellStyle.SelectionForeColor;
            }
        }

        private void OnColumnWidthChangedInternal(object sender, DataGridViewColumnEventArgs e)
        {
            if (disposed || loadingLayout || savingLayout || autoFittingWidths)
            {
                return;
            }

            SetBaseWidth(e.Column, e.Column.Width);
            ScheduleLayoutSave();
        }

        private void ColumnWidthSaveTimerTick(object sender, EventArgs e)
        {
            columnWidthSaveTimer.Stop();
            if (!columnWidthSavePending)
            {
                return;
            }

            if (SaveLayout())
            {
                columnWidthSavePending = false;
            }
        }

        private void ApplyFont(Font font)
        {
            Font previous = appliedFont;
            ownedFonts.Add(font);
            appliedFont = font;
            SuspendLayout();
            try
            {
                Font = font;
            }
            finally
            {
                ResumeLayout(true);
            }

            if (previous != null && !ReferenceEquals(previous, font))
            {
                ownedFonts.Remove(previous);
                try
                {
                    previous.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private int ComputeRowHeight()
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            int height = (int)Math.Ceiling(Font.GetHeight(dpi)) + RowHeightPadding;
            return height < 1 ? 1 : height;
        }

        private int ComputeHeaderHeight()
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            int height = (int)Math.Ceiling(Font.GetHeight(dpi)) + HeaderHeightPadding;
            return height < 1 ? 1 : height;
        }

        private void UpdateRowHeights()
        {
            if (disposed)
            {
                return;
            }

            // 表头高度手动计算：AutoSize 会在每次字号变化时测量所有列（71 列实测约 35–40 ms/次），
            // 手动按字体高度计算后，字号缩放从约 40 ms 降到 2–3 ms。
            if (ColumnHeadersHeightSizeMode != DataGridViewColumnHeadersHeightSizeMode.AutoSize)
            {
                int headerHeight = ComputeHeaderHeight();
                if (ColumnHeadersHeight != headerHeight)
                {
                    ColumnHeadersHeight = headerHeight;
                }
            }

            int height = ComputeRowHeight();
            if (RowTemplate.Height != height)
            {
                RowTemplate.Height = height;
            }
            UpdateVisibleRowHeights(height);
        }

        /// <summary>
        /// 只调整当前可见行的行高：1000 行时逐行设置 <see cref="DataGridViewRow.Height"/> 会触发
        /// 大量布局（实测每次字号变化 1 秒以上）。不可见行保持旧高度，滚动进入可见区时由
        /// Scroll/Resize 处理器补到当前高度；行模板保证之后新增的行直接使用新高度。
        /// </summary>
        /// <param name="height">目标行高（像素）。</param>
        private void UpdateVisibleRowHeights(int height)
        {
            if (disposed || !IsHandleCreated || Rows.Count == 0)
            {
                return;
            }

            int first;
            int count;
            if (!TryGetVisibleRowRange(out first, out count))
            {
                return;
            }

            int last = Math.Min(Rows.Count, first + count);
            SuspendLayout();
            try
            {
                for (int i = first; i < last; i++)
                {
                    if (Rows[i].Height != height)
                    {
                        Rows[i].Height = height;
                    }
                }
            }
            finally
            {
                ResumeLayout();
            }
        }

        private void OnScrollUpdateRowHeights(object sender, ScrollEventArgs e)
        {
            UpdateVisibleRowHeights(ComputeRowHeight());
        }

        private void OnResizeUpdateRowHeights(object sender, EventArgs e)
        {
            UpdateVisibleRowHeights(ComputeRowHeight());
        }

        private static void WriteAllTextAtomic(string path, string content)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = fullPath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Replace(tempPath, fullPath, fullPath + ".bak", true);
                    }
                    catch (Exception)
                    {
                        File.Copy(tempPath, fullPath, true);
                        File.Delete(tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static void BackupLegacyFile(string path, bool exists)
        {
            if (!exists || string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Copy(path, path + ".bak", true);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.WriteLog("备份旧布局失败(" + path + "): " + ex.Message, "Layout", "IndustrialDataGridView");
            }
        }

        private void EnsureUiThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThreadId)
            {
                throw new InvalidOperationException(
                    "该操作只能在创建控件的 UI 线程调用；后台线程请使用 PostUpdates/PostCellUpdate。");
            }
        }

        private bool IsReallyVisible()
        {
            if (!Visible || !IsHandleCreated)
            {
                return false;
            }

            Form form = FindForm();
            // 没有父窗体（纯代码测试/临时控件）不算“现场可见”，否则构造期 ShowRows 会误抛异常。
            return form != null && form.Visible;
        }

        private sealed class LayoutFileModel
        {
            [JsonProperty("version")]
            public int Version { get; set; }

            [JsonProperty("dpi")]
            public int Dpi { get; set; }

            [JsonProperty("columns")]
            public List<LayoutColumnModel> Columns { get; set; }
        }

        private sealed class LayoutColumnModel
        {
            [JsonProperty("key")]
            public string Key { get; set; }

            [JsonProperty("visible")]
            public bool Visible { get; set; }

            [JsonProperty("width")]
            public int Width { get; set; }
        }

        private sealed class LegacyColumnWidthEntry
        {
            [JsonProperty("ID")]
            public int ID { get; set; }

            [JsonProperty("width")]
            public int Width { get; set; }
        }
    }

    /// <summary>
    /// 一次单元格增量更新：行索引 + 列索引 + 新值。
    /// </summary>
    public readonly struct CellUpdate
    {
        /// <summary>创建一个单元格更新。</summary>
        /// <param name="rowIndex">数据行索引（DataTable 数据顺序，不受显示排序影响）。</param>
        /// <param name="columnIndex">列索引（与 <see cref="ColumnStruct"/> 顺序一致，可用 <see cref="IndustrialDataGridView{T}.GetColumnIndex"/> 从列键取得）。</param>
        /// <param name="value">新值；<see langword="null"/> 按 <see cref="DBNull"/> 处理。</param>
        public CellUpdate(int rowIndex, int columnIndex, object value)
        {
            RowIndex = rowIndex;
            ColumnIndex = columnIndex;
            Value = value;
        }

        /// <summary>数据行索引（DataTable 数据顺序，不受显示排序影响）。</summary>
        public int RowIndex { get; }

        /// <summary>列索引。</summary>
        public int ColumnIndex { get; }

        /// <summary>新值。</summary>
        public object Value { get; }
    }
}

