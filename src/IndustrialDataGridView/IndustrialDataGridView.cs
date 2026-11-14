using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
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
        /// 数据列在构造时固定、按列定义允许排序、不允许用户编辑；每控件一个布局文件；
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
        private const int MaxDrainBatchSize = 512;
        private const int DrainTimeBudgetMs = 8;
        private const long CellErrorLogIntervalTicks = 5 * TimeSpan.TicksPerSecond;

        private readonly DataTable dataTable;
        private readonly ColumnStruct[] columnDefinitions;
        private readonly IReadOnlyList<ColumnStruct> columnDefinitionsView;
        private readonly Dictionary<string, int> columnIndexByKey;
        private readonly int ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly System.Windows.Forms.Timer columnWidthSaveTimer = new System.Windows.Forms.Timer();
        private readonly ConcurrentQueue<PendingCellUpdate> pendingUpdates = new ConcurrentQueue<PendingCellUpdate>();
        private readonly System.Windows.Forms.Timer drainTimer = new System.Windows.Forms.Timer();
        private readonly List<RowUpdateTarget> rowUpdateTargets = new List<RowUpdateTarget>();
        private RowUpdateTarget[] publishedRowTargets = new RowUpdateTarget[0];
        private readonly List<T> rowItems = new List<T>();
        private readonly Dictionary<DataRow, T> rowMap = new Dictionary<DataRow, T>();
        private readonly List<DataGridViewColumn> lockedColumns = new List<DataGridViewColumn>();
        private readonly Dictionary<string, int> baseColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private Font appliedFont;
        private bool columnWidthSavePending;
        private int layoutSaveRetryCount;
        private bool loadingLayout;
        private bool savingLayout;
        private bool layoutWidthsChangedByUser;
        private int widthsDpi = LogicalDpi;
        private volatile bool disposed;
        private bool mutatingRows;
        private bool selectionChangePending;
        private bool updatingRowHeights;
        private bool updatingStyleMetrics;
        private bool styleMetricsPending;
        private bool rowMappingNeedsValidation;
        private DataTable conversionTable;
        private DataRow conversionRow;
        private bool lockedColumnRestorePending;
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
        private Exception lastCellUpdateException;
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
            drainTimer.Interval = 1;
            drainTimer.Tick += delegate { drainTimer.Stop(); DrainPending(); };
            ColumnWidthChanged += OnColumnWidthChangedInternal;
            CellFormatting += OnCellFormattingInternal;
            ColumnDisplayIndexChanged += OnColumnDisplayIndexChangedInternal;
            ColumnStateChanged += OnColumnStateChangedInternal;
            Scroll += OnScrollUpdateRowHeights;
            Resize += OnResizeUpdateRowHeights;
            CellStyleContentChanged += delegate { ScheduleStyleMetrics(); };
            DefaultCellStyleChanged += delegate { ScheduleStyleMetrics(); };
            ColumnHeadersDefaultCellStyleChanged += delegate { ScheduleStyleMetrics(); };
            RowsDefaultCellStyleChanged += delegate { ScheduleStyleMetrics(); };
            AlternatingRowsDefaultCellStyleChanged += delegate { ScheduleStyleMetrics(); };
            ColumnDefaultCellStyleChanged += delegate { ScheduleStyleMetrics(); };
            CellStyleChanged += delegate { ScheduleStyleMetrics(); };
            RowDefaultCellStyleChanged += delegate { ScheduleStyleMetrics(); };
            RowsAdded += OnRowsAddedInitializeHeight;
            dataTable.RowChanged += delegate(object sender, DataRowChangeEventArgs e) {
                if (!mutatingRows && (e.Action == DataRowAction.Add || e.Action == DataRowAction.Rollback)) rowMappingNeedsValidation = true;
            };
            dataTable.RowDeleted += delegate { if (!mutatingRows) rowMappingNeedsValidation = true; };
            dataTable.TableCleared += delegate { if (!mutatingRows) rowMappingNeedsValidation = true; };
        }

        /// <summary>构造时传入的列定义（只读视图，顺序与 <see cref="DataGridView.Columns"/> / <see cref="Table"/> 的列顺序一致）。</summary>
        public IReadOnlyList<ColumnStruct> ColumnDefinitions
        {
            get { return columnDefinitionsView; }
        }

        /// <summary>
        /// 固定列集合。列在构造时创建，设计器不应把构造函数中的列再次序列化到 InitializeComponent，
        /// 否则运行时会重复添加列。
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new DataGridViewColumnCollection Columns
        {
            get { return base.Columns; }
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
                RestoreLockedColumnOrder();
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
                int index = GetColumnIndex(value);
                if (!string.IsNullOrEmpty(value) && index < 0)
                {
                    throw new ArgumentException("报警列不存在: " + value, nameof(value));
                }
                if (index >= 0 && columnDefinitions[index].columnType != typeof(bool)
                    && columnDefinitions[index].columnType != typeof(string)
                    && !IsNumericType(columnDefinitions[index].columnType))
                {
                    throw new ArgumentException("报警列必须为布尔、数值或布尔文本类型。", nameof(value));
                }
                alarmColumnKey = value;
                alarmColumnIndex = index;
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
        /// 外键验证要求父子表位于同一 DataSet，并启用 EnforceConstraints；独立表外键不受支持。
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

        /// <summary>布局操作的非致命提示，例如从备份恢复或旧文件清理失败；成功保存后清空。</summary>
        public string LastLayoutWarning { get; private set; }

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

        /// <summary>因队列已满或目标行已删除/替换/重建而丢弃的增量更新条数。</summary>
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
        /// 外键验证要求父子表位于同一 DataSet 且 EnforceConstraints=true；独立表跨表外键不受支持。
        /// 预验证复制 DataSet 的结构，但只复制通过外键/关系连通的关联表数据，不复制无关归档或日志行。
        /// </remarks>
        public void ShowRows(IReadOnlyList<T> rows)
        {
            EnsureUiThread();
            EnsureSupportedForeignKeys();
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

            // 关联表的数据也是外键验证的一部分；仅 Clone 当前表不能验证跨表约束。
            var items = new List<T>(rows.Count);
            var valuesList = new List<object[]>(rows.Count);
            using (DataSet stagedSet = CreateValidationDataSet())
            using (DataTable staged = stagedSet == null ? dataTable.Clone()
                : stagedSet.Tables[dataTable.TableName, dataTable.Namespace])
            {
                bool enforce = dataTable.DataSet != null && dataTable.DataSet.EnforceConstraints;
                if (stagedSet != null) stagedSet.EnforceConstraints = false;
                staged.Rows.Clear();
                for (int i = 0; i < rows.Count; i++)
                {
                    T item = rows[i];
                    staged.Rows.Add(GetRowValuesChecked(item, i));
                    items.Add(item);
                }
                if (stagedSet != null) stagedSet.EnforceConstraints = enforce;
                foreach (DataRow row in staged.Rows) valuesList.Add(row.ItemArray);
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
                        InvalidateRowUpdateTargets();
                        rowMap.Clear();
                        rowItems.Clear();
                        for (int i = 0; i < valuesList.Count; i++)
                        {
                            DataRow dataRow = dataTable.Rows.Add(valuesList[i]);
                            rowMap[dataRow] = items[i];
                            rowItems.Add(items[i]);
                            rowUpdateTargets.Add(new RowUpdateTarget(dataRow));
                        }
                        rowMappingNeedsValidation = false;
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
                ResumeLayout(true);
                CompleteRowMutation();
            }
            ForceFirstColumnVisible();
        }

        private void EnsureSupportedForeignKeys()
        {
            foreach (Constraint constraint in dataTable.Constraints)
            {
                var foreignKey = constraint as ForeignKeyConstraint;
                if (foreignKey != null && (dataTable.DataSet == null || foreignKey.RelatedTable.DataSet != dataTable.DataSet))
                    throw new InvalidOperationException("外键验证要求父子表位于同一 DataSet；独立 DataTable 上的外键不受支持。");
            }
        }

        private DataSet CreateValidationDataSet()
        {
            DataSet source = dataTable.DataSet;
            if (source == null) return null;
            // 保留关系/表达式/命名空间等完整结构；无关大表仅有空结构，不导入数据。
            var neighbors = new Dictionary<DataTable, HashSet<DataTable>>();
            var externalReferences = new HashSet<DataTable>();
            foreach (DataTable table in source.Tables) neighbors.Add(table, new HashSet<DataTable>());
            foreach (DataTable table in source.Tables)
                foreach (Constraint constraint in table.Constraints)
                    if (constraint is ForeignKeyConstraint foreignKey)
                    {
                        if (!neighbors.ContainsKey(foreignKey.RelatedTable))
                        {
                            externalReferences.Add(table);
                            continue;
                        }
                        neighbors[table].Add(foreignKey.RelatedTable);
                        neighbors[foreignKey.RelatedTable].Add(table);
                    }
            // createConstraints=false 的关系也可能供 Parent/Child 表达式使用。
            foreach (DataRelation relation in source.Relations)
            {
                neighbors[relation.ParentTable].Add(relation.ChildTable);
                neighbors[relation.ChildTable].Add(relation.ParentTable);
            }
            var related = new HashSet<DataTable> { dataTable };
            var pending = new Queue<DataTable>(); pending.Enqueue(dataTable);
            while (pending.Count > 0)
            {
                DataTable current = pending.Dequeue();
                if (externalReferences.Contains(current))
                    throw new InvalidOperationException("当前表的关联数据含外部外键；相关父子表必须位于同一 DataSet。");
                foreach (DataTable next in neighbors[current])
                    if (related.Add(next)) pending.Enqueue(next);
            }

            // DataSet.Clone 本身也无法克隆指向集合外部的外键。此时只重建有效连通分量的结构。
            DataSet staged = externalReferences.Count == 0 ? source.Clone() : CloneConnectedSchema(source, related);
            try
            {
                staged.EnforceConstraints = false;
                foreach (DataTable table in source.Tables)
                {
                    if (table == dataTable || !related.Contains(table)) continue;
                    DataTable target = staged.Tables[table.TableName, table.Namespace];
                    foreach (DataRow row in table.Rows) target.ImportRow(row);
                }
                return staged;
            }
            catch { staged.Dispose(); throw; }
        }

        private static DataSet CloneConnectedSchema(DataSet source, HashSet<DataTable> related)
        {
            var result = new DataSet(source.DataSetName) { Namespace = source.Namespace, Prefix = source.Prefix,
                Locale = source.Locale, CaseSensitive = source.CaseSensitive, EnforceConstraints = false };
            var copies = new Dictionary<DataTable, DataTable>();
            try
            {
                foreach (DataTable table in source.Tables)
                {
                    if (!related.Contains(table)) continue;
                    bool hasExpressions = false;
                    foreach (DataColumn column in table.Columns) hasExpressions |= column.Expression.Length != 0;
                    DataTable copy;
                    if (!hasExpressions)
                    {
                        // 保留表内约束、自增状态等标准 Clone 行为；跨表外键稍后补齐。
                        copy = table.Clone();
                        copy.Namespace = table.Namespace;
                    }
                    else
                    {
                        // 单表 Clone 会过早绑定 Parent/Child 表达式；等关系齐全后再恢复表达式。
                        copy = new DataTable(table.TableName) { Namespace = table.Namespace, Prefix = table.Prefix,
                            Locale = table.Locale, CaseSensitive = table.CaseSensitive, MinimumCapacity = table.MinimumCapacity };
                        foreach (DataColumn column in table.Columns)
                        {
                            var target = new DataColumn(column.ColumnName, column.DataType) { Caption = column.Caption,
                                AllowDBNull = column.AllowDBNull, ReadOnly = column.ReadOnly, MaxLength = column.MaxLength,
                                Namespace = column.Namespace, Prefix = column.Prefix, ColumnMapping = column.ColumnMapping,
                                AutoIncrementSeed = column.AutoIncrementSeed, AutoIncrementStep = column.AutoIncrementStep,
                                AutoIncrement = column.AutoIncrement, DefaultValue = column.DefaultValue };
                            if (column.DataType == typeof(DateTime)) target.DateTimeMode = column.DateTimeMode;
                            copy.Columns.Add(target);
                        }
                        foreach (Constraint constraint in table.Constraints)
                            if (constraint is UniqueConstraint unique)
                            {
                                var columns = new DataColumn[unique.Columns.Length];
                                for (int i = 0; i < columns.Length; i++) columns[i] = copy.Columns[unique.Columns[i].Ordinal];
                                copy.Constraints.Add(new UniqueConstraint(unique.ConstraintName, columns, unique.IsPrimaryKey));
                            }
                    }
                    result.Tables.Add(copy); copies.Add(table, copy);
                }
                foreach (DataTable table in related)
                    foreach (Constraint constraint in table.Constraints)
                        if (constraint is ForeignKeyConstraint key && !copies[table].Constraints.Contains(key.ConstraintName))
                            copies[table].Constraints.Add(new ForeignKeyConstraint(key.ConstraintName,
                                MapColumns(key.RelatedColumns, copies), MapColumns(key.Columns, copies)) {
                                DeleteRule = key.DeleteRule, UpdateRule = key.UpdateRule, AcceptRejectRule = key.AcceptRejectRule });
                foreach (DataRelation relation in source.Relations)
                    if (related.Contains(relation.ParentTable) && related.Contains(relation.ChildTable))
                        result.Relations.Add(new DataRelation(relation.RelationName, MapColumns(relation.ParentColumns, copies),
                            MapColumns(relation.ChildColumns, copies), false) { Nested = relation.Nested });
                foreach (DataTable table in related)
                    foreach (DataColumn column in table.Columns)
                        if (column.Expression.Length != 0) copies[table].Columns[column.Ordinal].Expression = column.Expression;
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static DataColumn[] MapColumns(DataColumn[] columns, Dictionary<DataTable, DataTable> copies)
        {
            var result = new DataColumn[columns.Length];
            for (int i = 0; i < columns.Length; i++) result[i] = copies[columns[i].Table].Columns[columns[i].Ordinal];
            return result;
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
            EnsureRowMapping(-1);
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
                rowUpdateTargets.Add(new RowUpdateTarget(dataRow));
            }
            finally
            {
                CompleteRowMutation();
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
            EnsureRowMapping(-1);
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
                rowUpdateTargets.Insert(rowIndex, new RowUpdateTarget(dataRow));
            }
            finally
            {
                CompleteRowMutation();
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
                rowUpdateTargets[rowIndex].Active = false;
                rowUpdateTargets.RemoveAt(rowIndex);
            }
            finally
            {
                CompleteRowMutation();
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
            bool wasSelected = IsDataRowSelected(dataRow);
            mutatingRows = true;
            try
            {
                dataRow.ItemArray = values;
                rowItems[rowIndex] = item;
                rowMap[dataRow] = item;
                rowUpdateTargets[rowIndex].Active = false;
                rowUpdateTargets[rowIndex] = new RowUpdateTarget(dataRow);
                selectionChangePending |= wasSelected;
            }
            finally
            {
                CompleteRowMutation();
            }
            if (alarmColumnIndex >= 0)
            {
                InvalidateDataRow(dataRow);
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
            if (!mutatingRows) EnsureRowMapping(-1);
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
            if (view != null && rowMap.TryGetValue(view.Row, out item)) return true;
            throw new InvalidOperationException("选中行的对象映射已失效；请使用控件行操作接口，不要直接替换 Table.Rows。");
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
                    InvalidateRowUpdateTargets();
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
                ResumeLayout(true);
                CompleteRowMutation();
            }
            ForceFirstColumnVisible();
        }

        /// <summary>行操作期间合并选中通知，确保宿主读取到已完成的对象映射。</summary>
        protected override void OnSelectionChanged(EventArgs e)
        {
            if (mutatingRows)
            {
                selectionChangePending = true;
                return;
            }
            base.OnSelectionChanged(e);
        }

        /// <summary>派生类重写 OnSelectionChanged 时可用本属性跳过行操作中的中间状态。</summary>
        protected bool IsUpdatingRows { get { return mutatingRows; } }

        private void CompleteRowMutation()
        {
            Volatile.Write(ref publishedRowTargets, rowUpdateTargets.ToArray());
            mutatingRows = false;
            if (selectionChangePending)
            {
                selectionChangePending = false;
                OnSelectionChanged(EventArgs.Empty);
            }
        }

        private bool IsDataRowSelected(DataRow dataRow)
        {
            foreach (DataGridViewRow row in SelectedRows)
                if ((row.DataBoundItem as DataRowView)?.Row == dataRow) return true;
            if (SelectedRows.Count == 0)
                foreach (DataGridViewCell cell in SelectedCells)
                    if ((Rows[cell.RowIndex].DataBoundItem as DataRowView)?.Row == dataRow) return true;
            return false;
        }

        private void InvalidateDataRow(DataRow dataRow)
        {
            int first, count;
            if (!TryGetVisibleRowRange(out first, out count)) return;
            for (int i = first; i < first + count; i++)
                if ((Rows[i].DataBoundItem as DataRowView)?.Row == dataRow) InvalidateRow(i);
        }

        private void InvalidateRowUpdateTargets()
        {
            foreach (RowUpdateTarget target in rowUpdateTargets)
            {
                target.Active = false;
            }
            rowUpdateTargets.Clear();
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
            if (!mutatingRows) EnsureRowMapping(-1);
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
                throw new ArgumentException(LastCellUpdateError, nameof(value), lastCellUpdateException);
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
        /// 尝试写入一个单元格（UI 线程）。索引越界、类型转换、只读列或约束失败返回 false 并记账。
        /// 线程错误、外部破坏映射和非数据层程序异常仍会抛出。
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
        /// 批量写入单元格（UI 线程）。越界、类型转换、只读列及约束失败的项被跳过并计数，不中断整批。
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
        /// <remarks>
        /// 行号按投递时的行快照解析；后续插入/删除不会改变目标，目标被删除、替换或重建后更新将丢弃。
        /// 队列上限约 10 万条；丢弃计入 <see cref="DroppedUpdateCount"/>。调用方应使用当前数据行号投递。
        /// </remarks>
        public void PostCellUpdate(CellUpdate update)
        {
            EnqueueUpdate(update, Volatile.Read(ref publishedRowTargets));
        }

        /// <summary>稳定的行句柄；可传给后台线程。删除、替换或重建该行后句柄失效。</summary>
        public sealed class RowHandle
        {
            internal readonly object Owner;
            internal readonly object Target;
            internal RowHandle(object owner, object target) { Owner = owner; Target = target; }
        }

        /// <summary>在 UI 线程取得稳定行句柄，避免生产者缓存的旧行号在投递前发生错位。</summary>
        public RowHandle GetRowHandle(int rowIndex)
        {
            EnsureUiThread();
            ValidateRowIndex(rowIndex);
            return new RowHandle(this, rowUpdateTargets[rowIndex]);
        }

        /// <summary>按稳定行句柄投递；插入、删除其它行或排序不改变目标，失效句柄计入丢弃。</summary>
        public void PostCellUpdate(RowHandle row, int columnIndex, object value)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (!ReferenceEquals(row.Owner, this)) throw new ArgumentException("行句柄属于其它控件。", nameof(row));
            EnqueueUpdate(new PendingCellUpdate((RowUpdateTarget)row.Target, new CellUpdate(-1, columnIndex, value)));
        }

        private void EnqueueUpdate(CellUpdate update, RowUpdateTarget[] targets)
        {
            RowUpdateTarget target = update.RowIndex >= 0 && update.RowIndex < targets.Length
                ? targets[update.RowIndex] : null;
            EnqueueUpdate(new PendingCellUpdate(target, update));
        }

        private void EnqueueUpdate(PendingCellUpdate update)
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
        /// 线程安全的批量更新入口：任意线程可调用；整批使用同一份行快照，分批在 UI 线程应用。
        /// </summary>
        /// <param name="updates">要应用的更新集合；为 <see langword="null"/> 时什么都不做。</param>
        public void PostUpdates(IEnumerable<CellUpdate> updates)
        {
            if (updates == null)
            {
                return;
            }

            RowUpdateTarget[] targets = Volatile.Read(ref publishedRowTargets);
            foreach (CellUpdate update in updates)
            {
                EnqueueUpdate(update, targets);
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
            if (column.Index == 0 || IsColumnLocked(columnKey))
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
        /// 主文件不可用时尝试 .bak；成功返回 true，备份恢复记入 LastLayoutWarning。
        /// 两者均不可用时返回 false，并回退为“全部显示 + 默认列宽 + 定义顺序”；未设置文件名时不更改界面。
        /// </returns>
        /// <remarks>前 <see cref="LockedColumnCount"/> 列始终强制可见；文件中缺失的列按默认宽度显示并排在已知列之后。</remarks>
        public bool LoadLayout()
        {
            EnsureUiThread();
            LastLayoutError = null;
            LastLayoutWarning = null;
            if (string.IsNullOrEmpty(LayoutFile))
            {
                return false;
            }

            try
            {
                if (!File.Exists(LayoutFile) && !File.Exists(LayoutFile + ".bak"))
                {
                    ApplyDefaults();
                    return false;
                }

                LayoutFileModel model;
                try
                {
                    model = ReadLayoutModel(LayoutFile);
                }
                catch (Exception primaryError) when (primaryError is IOException || primaryError is UnauthorizedAccessException
                    || primaryError is JsonException || primaryError is InvalidDataException)
                {
                    if (!File.Exists(LayoutFile + ".bak")) throw;
                    model = ReadLayoutModel(LayoutFile + ".bak");
                    LastLayoutWarning = "主布局不可用，已从 .bak 恢复: " + primaryError.Message;
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

        private static LayoutFileModel ReadLayoutModel(string path)
        {
            var model = JsonConvert.DeserializeObject<LayoutFileModel>(File.ReadAllText(path));
            if (model == null || model.Version <= 0 || model.Version > LayoutFileVersion)
                throw new InvalidDataException("布局文件版本无效。");
            return model;
        }

        /// <summary>
        /// 把当前的列宽、显隐与显示顺序写入 <see cref="LayoutFile"/>（版本 2；列宽为 96 DPI 逻辑像素，
        /// 加载时按 DeviceDpi/96 缩放；原子写入：临时文件 + 替换 + .bak 备份）。
        /// </summary>
        /// <returns>写入成功返回 <see langword="true"/>；未设置文件名、没有列或写入失败返回 <see langword="false"/>。</returns>
        public bool SaveLayout()
        {
            EnsureUiThread();
            RestoreLockedColumnOrder();
            LastLayoutError = null;
            if (string.IsNullOrEmpty(LayoutFile) || Columns.Count == 0)
            {
                return false;
            }

            savingLayout = true;
            try
            {
                LayoutFileModel model = CaptureLayoutModel();

                string json = JsonConvert.SerializeObject(model, Formatting.Indented);
                WriteAllTextAtomic(LayoutFile, json);
                LastLayoutWarning = null;
                columnWidthSavePending = false;
                layoutSaveRetryCount = 0;
                columnWidthSaveTimer.Stop();
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

        private LayoutFileModel CaptureLayoutModel()
        {
            var model = new LayoutFileModel { Version = LayoutFileVersion, Dpi = LogicalDpi,
                Columns = new List<LayoutColumnModel>(Columns.Count) };
            foreach (DataGridViewColumn column in GetColumnsInDisplayOrder())
                model.Columns.Add(new LayoutColumnModel { Key = column.Name, Visible = column.Visible,
                    Width = ToLogicalWidth(GetBaseWidth(column)) });
            return model;
        }

        /// <summary>
        /// 迁移旧版的“列宽 JSON（ID/width）+ 显隐 CSV（1/0）”到新的单文件布局。
        /// </summary>
        /// <param name="legacyWidthFile">旧列宽 JSON 路径；为空或不存在时跳过列宽。</param>
        /// <param name="legacyShownFile">旧显隐 CSV 路径；为空或不存在时跳过显隐。</param>
        /// <returns>迁移并写入新布局成功返回 <see langword="true"/>；新布局已存在、旧文件不存在或读写失败返回 <see langword="false"/>。</returns>
        /// <remarks>
        /// 旧格式按“当前列顺序”映射（旧列宽是保存时 DPI 下的物理像素）；迁移后写为新版 96 DPI 逻辑宽度，
        /// 先复制旧文件到 <c>*.bak</c>；新布局写入成功后清理旧文件，清理失败记入 LastLayoutWarning。
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

            LastLayoutWarning = null;
            try
            {
                // 先解析并准备备份；应用/发布失败由事务辅助方法恢复原 UI 和保存状态。
                LayoutFileModel model = CaptureLayoutModel();
                var byKey = new Dictionary<string, LayoutColumnModel>(StringComparer.OrdinalIgnoreCase);
                foreach (LayoutColumnModel column in model.Columns)
                {
                    column.Width = ToLogicalWidth(GetDefaultColumnWidth(column.Key));
                    column.Visible = true;
                    byKey.Add(column.Key, column);
                }
                if (hasWidth)
                {
                    var widths = JsonConvert.DeserializeObject<List<LegacyColumnWidthEntry>>(File.ReadAllText(legacyWidthFile));
                    if (widths != null)
                        foreach (LegacyColumnWidthEntry entry in widths)
                            if (entry != null && entry.ID >= 0 && entry.ID < Columns.Count && entry.Width > 0)
                                byKey[Columns[entry.ID].Name].Width = ToLogicalWidth(Math.Max(Columns[entry.ID].MinimumWidth, Math.Min(entry.Width, 65535)));
                }
                if (hasShown)
                {
                    string[] parts = File.ReadAllText(legacyShownFile).Split(',');
                    for (int i = 0; i < parts.Length && i < Columns.Count; i++)
                        byKey[Columns[i].Name].Visible = parts[i].Trim() != "0" || i == 0 || IsColumnLocked(Columns[i].Name);
                }
                // 备份失败必须报告失败，并保留旧文件和原界面。
                if (hasWidth) File.Copy(legacyWidthFile, legacyWidthFile + ".bak", true);
                if (hasShown) File.Copy(legacyShownFile, legacyShownFile + ".bak", true);
                ApplyAndPublishMigratedLayout(model);
                loadedLayout = model;
                columnWidthSavePending = false;
                columnWidthSaveTimer.Stop();
                try
                {
                    if (hasWidth) File.Delete(legacyWidthFile);
                    if (hasShown) File.Delete(legacyShownFile);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    LastLayoutWarning = "布局已迁移并备份，但旧文件清理失败: " + ex.Message;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastLayoutError = "迁移旧布局失败: " + ex.Message;
                return false;
            }
        }

        private void ApplyAndPublishMigratedLayout(LayoutFileModel model)
        {
            LayoutFileModel previous = CaptureLayoutModel();
            LayoutFileModel previousLoaded = loadedLayout;
            int previousLoadedDpi = loadedLayoutDpi, previousWidthsDpi = widthsDpi;
            bool previousDirty = layoutWidthsChangedByUser, previousPending = columnWidthSavePending;
            bool previousTimer = columnWidthSaveTimer.Enabled;
            int previousInterval = columnWidthSaveTimer.Interval, previousRetry = layoutSaveRetryCount;
            var minimums = new int[Columns.Count];
            var physicalWidths = new int[Columns.Count];
            var previousBaseWidths = new Dictionary<string, int>(baseColumnWidths, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Columns.Count; i++) { minimums[i] = Columns[i].MinimumWidth; physicalWidths[i] = Columns[i].Width; }
            columnWidthSaveTimer.Stop();
            try
            {
                ApplyLayoutModel(model);
                // 仅应用成功才发布新文件；目标若被其它调用创建，禁止覆盖。
                WriteAllTextAtomic(LayoutFile, JsonConvert.SerializeObject(model, Formatting.Indented), false);
            }
            catch (Exception applyError)
            {
                try
                {
                    ApplyLayoutModel(previous);
                    loadingLayout = true;
                    for (int i = 0; i < Columns.Count; i++)
                    {
                        Columns[i].MinimumWidth = minimums[i];
                        Columns[i].Width = physicalWidths[i];
                    }
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(applyError.Message + "；恢复原布局也失败: " + rollbackError.Message, applyError);
                }
                finally
                {
                    loadingLayout = false;
                    loadedLayout = previousLoaded; loadedLayoutDpi = previousLoadedDpi; widthsDpi = previousWidthsDpi;
                    baseColumnWidths.Clear();
                    foreach (var width in previousBaseWidths) baseColumnWidths.Add(width.Key, width.Value);
                    layoutWidthsChangedByUser = previousDirty; columnWidthSavePending = previousPending;
                    layoutSaveRetryCount = previousRetry; columnWidthSaveTimer.Interval = previousInterval;
                    if (previousTimer) columnWidthSaveTimer.Start();
                }
                throw;
            }
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
            styleMetricsPending = false;
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
            RestoreLockedColumnOrder();
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
            styleMetricsPending = false;
            drainTimer.Stop();
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
                // 用户换序后可能尚未处理延后恢复消息，关闭前仍需保存最终合法布局。
                RestoreLockedColumnOrder();
                disposed = true;
                try
                {
                    columnWidthSaveTimer.Stop();
                    columnWidthSaveTimer.Dispose();
                    drainTimer.Stop();
                    drainTimer.Dispose();
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

                InvalidateRowUpdateTargets();
                Volatile.Write(ref publishedRowTargets, new RowUpdateTarget[0]);
                PendingCellUpdate update;
                while (pendingUpdates.TryDequeue(out update))
                {
                }
                Interlocked.Exchange(ref pendingUpdateCount, 0);
                conversionTable?.Dispose();
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
            // 补齐默认样式的继承值，避免框架 getter 每次返回临时副本，
            // 导致宿主通过 DefaultCellStyle.Font/Padding 设置的值丢失。
            DefaultCellStyle = DefaultCellStyle;
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
            for (int i = 0; i < values.Length; i++)
            {
                copy[i] = values[i] ?? DBNull.Value;
            }
            return copy;
        }

        private bool IsValidCell(int rowIndex, int columnIndex)
        {
            EnsureRowMapping(rowIndex);
            return rowIndex >= 0 && rowIndex < dataTable.Rows.Count
                && columnIndex >= 0 && columnIndex < dataTable.Columns.Count;
        }

        private void EnsureRowMapping(int rowIndex)
        {
            EnsureSupportedForeignKeys();
            if (!ReferenceEquals(DataSource, dataTable) || dataTable.Rows.Count != rowItems.Count
                || rowUpdateTargets.Count != rowItems.Count || dataTable.Columns.Count != columnDefinitions.Length
                || Columns.Count != columnDefinitions.Length
                || (rowIndex >= 0 && rowIndex < rowUpdateTargets.Count
                    && !ReferenceEquals(rowUpdateTargets[rowIndex].Row, dataTable.Rows[rowIndex])))
                throw new InvalidOperationException("数据绑定或行列集合被外部修改；请使用控件的行操作接口，保持固定列和原 DataSource。");
            if (rowMappingNeedsValidation)
            {
                for (int i = 0; i < rowItems.Count; i++)
                    if (!ReferenceEquals(rowUpdateTargets[i].Row, dataTable.Rows[i])
                        || dataTable.Rows[i].RowState == DataRowState.Deleted || !rowMap.ContainsKey(dataTable.Rows[i]))
                        throw new InvalidOperationException("数据行被外部替换或删除；请使用控件的行操作接口维护对象映射。");
                rowMappingNeedsValidation = false;
            }
        }

        private void ValidateRowIndex(int rowIndex)
        {
            EnsureRowMapping(rowIndex);
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
            return TrySetCellCore(dataTable.Rows[rowIndex], rowIndex, columnIndex, value);
        }

        private bool TrySetCellCore(DataRow row, int rowIndex, int columnIndex, object value)
        {
            // 后台队列不经过 ValidateRowIndex；不支持的外键配置也须记失败，避免静默接受。
            try { EnsureSupportedForeignKeys(); }
            catch (InvalidOperationException ex) { RecordCellError(ex.Message, ex); return false; }
            try
            {
                value = value ?? DBNull.Value;
                value = NormalizeCellValue(columnIndex, value);
                object oldValue = row[columnIndex];
                if (object.Equals(oldValue, value))
                {
                    return true;
                }
                row[columnIndex] = value;
                if (columnIndex == alarmColumnIndex)
                {
                    object newAlarmValue = row[columnIndex];
                    if (!AlarmValuesEqual(oldValue, newAlarmValue))
                    {
                        InvalidateDataRow(row);
                    }
                }
                return true;
            }
            catch (Exception ex) when (IsCellValueException(ex))
            {
                RecordCellError(
                    "行 " + rowIndex + " 列 " + columnIndex + " 写入 " + DescribeValue(value) + " 失败: " + ex.Message, ex);
                return false;
            }
        }

        private object NormalizeCellValue(int columnIndex, object value)
        {
            DataColumn column = dataTable.Columns[columnIndex];
            if (value == DBNull.Value || (column.DataType.IsInstanceOfType(value) && column.DataType != typeof(DateTime))) return value;
            // 使用未绑定、无约束的 DataColumn 转换，保持字符串、日期、Guid、TimeSpan、
            // SQL 类型等与 DataTable 完全相同的规则；不向实际绑定表发送试写通知。
            if (conversionTable != null && column.DataType == typeof(DateTime)
                && conversionTable.Columns[columnIndex].DateTimeMode != column.DateTimeMode)
            {
                conversionTable.Dispose(); conversionTable = null;
            }
            if (conversionTable == null)
            {
                conversionTable = new DataTable();
                foreach (DataColumn source in dataTable.Columns)
                {
                    DataColumn target = conversionTable.Columns.Add(source.ColumnName, source.DataType);
                    if (source.DataType == typeof(DateTime)) target.DateTimeMode = source.DateTimeMode;
                }
                conversionRow = conversionTable.NewRow();
            }
            conversionTable.Locale = dataTable.Locale;
            try { conversionRow[columnIndex] = value; return conversionRow[columnIndex]; }
            finally { conversionRow[columnIndex] = DBNull.Value; }
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
                || ex is DataException;
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

        private void RecordCellError(string message, Exception exception = null)
        {
            LastCellUpdateError = message;
            lastCellUpdateException = exception;
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
            if (disposed || Disposing)
            {
                return;
            }
            if (mutatingRows)
            {
                drainTimer.Start();
                return;
            }

            int failed = 0;
            var watch = Stopwatch.StartNew();
            try
            {
                PendingCellUpdate pending;
                int processed = 0;
                while (processed < MaxDrainBatchSize && watch.ElapsedMilliseconds < DrainTimeBudgetMs
                    && pendingUpdates.TryDequeue(out pending))
                {
                    processed++;
                    Interlocked.Decrement(ref pendingUpdateCount);
                    CellUpdate update = pending.Update;
                    if (pending.Target == null || update.ColumnIndex < 0 || update.ColumnIndex >= dataTable.Columns.Count)
                    {
                        RecordCellError("单元格索引越界: 行 " + update.RowIndex + ", 列 " + update.ColumnIndex);
                        failed++;
                        continue;
                    }
                    if (!pending.Target.Active)
                    {
                        Interlocked.Increment(ref droppedUpdateCount);
                        continue;
                    }
                    if (pending.Target.Row.RowState == DataRowState.Detached || !ReferenceEquals(DataSource, dataTable))
                    {
                        RecordCellError("目标行或数据绑定被外部修改，更新未应用。");
                        failed++;
                        continue;
                    }
                    if (!TrySetCellCore(pending.Target.Row, update.RowIndex, update.ColumnIndex, update.Value))
                    {
                        failed++;
                    }
                }
            }
            finally
            {
                if (!disposed && IsHandleCreated && !pendingUpdates.IsEmpty)
                {
                    // BeginInvoke 会在同一轮 WinForms 消息中继续执行新回调。
                    // 后续批次通过定时器让出消息循环，允许输入和绘制先执行。
                    drainTimer.Start();
                }
                else
                {
                    Interlocked.Exchange(ref drainScheduled, 0);
                    if (!pendingUpdates.IsEmpty)
                    {
                        ScheduleDrain();
                    }
                }
            }

            if (failed > 0)
            {
                LogCellErrors(failed);
            }
        }

        // 后台只读取不可变的行快照/目标引用；DataRow 和 Active 仅由 UI 线程访问。
        private sealed class RowUpdateTarget
        {
            public readonly DataRow Row;
            public bool Active = true;
            public RowUpdateTarget(DataRow row) { Row = row; }
        }

        private readonly struct PendingCellUpdate
        {
            public readonly RowUpdateTarget Target;
            public readonly CellUpdate Update;
            public PendingCellUpdate(RowUpdateTarget target, CellUpdate update)
            {
                Target = target;
                Update = update;
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
            if (Columns.Count > 0) Columns[0].Visible = true;
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
            UnfreezeColumns();
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
            lockedColumnRestorePending = false;
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

        private void ApplyLayoutModel(LayoutFileModel model, bool widthsOnly = false)
        {
            if (!widthsOnly) lockedColumnRestorePending = false;
            loadingLayout = true;
            try
            {
                if (!widthsOnly) UnfreezeColumns();
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
                    column.MinimumWidth = GetColumnMinimumWidth(column.Name, currentDpi);
                    LayoutColumnModel layout;
                    if (layoutByKey.TryGetValue(column.Name, out layout))
                    {
                        if (layout.Width > 0)
                        {
                            column.Width = ScaleWidth(layout.Width, sourceDpi, currentDpi, column.MinimumWidth);
                        }
                        SetBaseWidth(column, column.Width);
                        if (!widthsOnly) column.Visible = layout.Visible ?? true;
                    }
                    else
                    {
                        if (!widthsOnly) column.Visible = true;
                        int defaultWidth = GetDefaultColumnWidth(column.Name);
                        column.Width = defaultWidth;
                        SetBaseWidth(column, defaultWidth);
                    }
                }

                if (widthsOnly)
                {
                    AutoFitColumnHeaders();
                    return;
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
                ApplyLayoutModel(loadedLayout, true);
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

        private int GetColumnMinimumWidth(string key, int dpi)
        {
            int index = GetColumnIndex(key);
            int minimum = index >= 0 ? columnDefinitions[index].minimumWidth : 0;
            return ScaleWidth(minimum > 0 ? minimum : DefaultMinimumColumnWidth, LogicalDpi, dpi, 1);
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
            int padding = ScaleWidth(18, LogicalDpi, DeviceDpi, 1) + column.HeaderCell.InheritedStyle.Padding.Horizontal;
            if (column.SortMode == DataGridViewColumnSortMode.Automatic)
            {
                padding += ScaleWidth(18, LogicalDpi, DeviceDpi, 1);
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
            layoutSaveRetryCount = 0;
            columnWidthSaveTimer.Stop();
            columnWidthSaveTimer.Interval = ColumnWidthSaveDelayMs;
            columnWidthSaveTimer.Start();
        }

        private void OnColumnDisplayIndexChangedInternal(object sender, DataGridViewColumnEventArgs e)
        {
            if (disposed || loadingLayout || enforcingLockedColumns)
            {
                return;
            }

            if (lockedColumnRestorePending)
            {
                return;
            }
            lockedColumnRestorePending = true;
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(RestoreLockedColumnOrder));
            }
            // 无句柄时在 OnHandleCreated / SaveLayout 中恢复，不能在此事件内再次调整 DisplayIndex。
        }

        private void RestoreLockedColumnOrder()
        {
            if (disposed || !lockedColumnRestorePending)
            {
                return;
            }
            lockedColumnRestorePending = false;
            EnforceLockedColumnOrder();
            ScheduleLayoutSave();
        }

        private void UnfreezeColumns()
        {
            foreach (DataGridViewColumn column in Columns)
            {
                column.Frozen = false;
            }
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
            if ((e.StateChanged & DataGridViewElementStates.Visible) != 0)
            {
                ForceFirstColumnVisible();
                ScheduleLayoutSave();
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
                return;
            }

            if (alarm)
            {
                e.CellStyle.BackColor = alarmRowBackColor;
                e.CellStyle.ForeColor = alarmRowForeColor;
                if (!alarmRowSelectedBackColor.IsEmpty) e.CellStyle.SelectionBackColor = alarmRowSelectedBackColor;
                if (!alarmRowSelectedForeColor.IsEmpty) e.CellStyle.SelectionForeColor = alarmRowSelectedForeColor;
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
            else if (!disposed && ++layoutSaveRetryCount <= 3)
            {
                columnWidthSaveTimer.Interval = ColumnWidthSaveDelayMs * (1 << layoutSaveRetryCount);
                columnWidthSaveTimer.Start();
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
            int height = 0;
            var fontHeights = new Dictionary<Font, int>();
            foreach (DataGridViewColumn column in Columns)
                if (column.Visible)
                    for (int parity = 0; parity < 2; parity++)
                    {
                        // 字体和 Padding 各自按继承顺序合并，不依赖“列是否自定义字体”。
                        Font font = DefaultCellStyle.Font ?? Font;
                        Padding padding = DefaultCellStyle.Padding;
                        ApplyHeightStyle(column.DefaultCellStyle, ref font, ref padding);
                        ApplyHeightStyle(RowsDefaultCellStyle, ref font, ref padding);
                        if (parity == 1) ApplyHeightStyle(AlternatingRowsDefaultCellStyle, ref font, ref padding);
                        ApplyHeightStyle(RowTemplate.DefaultCellStyle, ref font, ref padding);
                        height = Math.Max(height, MeasureFontHeight(font, dpi, fontHeights) + padding.Vertical);
                    }
            if (height == 0) height = (int)Math.Ceiling(Font.GetHeight(dpi));
            return Math.Max(RowTemplate.MinimumHeight, height + ScaleWidth(RowHeightPadding, LogicalDpi, dpi, 1));
        }

        private static void ApplyHeightStyle(DataGridViewCellStyle style, ref Font font, ref Padding padding)
        {
            if (style.Font != null) font = style.Font;
            if (style.Padding != Padding.Empty) padding = style.Padding;
        }

        private static int MeasureFontHeight(Font font, int dpi, Dictionary<Font, int> cache)
        {
            int height;
            if (!cache.TryGetValue(font, out height))
            {
                height = (int)Math.Ceiling(font.GetHeight(dpi)); cache.Add(font, height);
            }
            return height;
        }

        private void OnRowsAddedInitializeHeight(object sender, DataGridViewRowsAddedEventArgs e)
        {
            if (disposed) return;
            int height = ComputeRowHeight();
            RowTemplate.Height = height;
            for (int i = e.RowIndex; i < e.RowIndex + e.RowCount && i < Rows.Count; i++)
            {
                int target = Math.Max(height, Rows[i].MinimumHeight);
                if (Rows[i].Height != target) Rows[i].Height = target;
            }
        }

        private int ComputeHeaderHeight()
        {
            int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
            int height = (int)Math.Ceiling((ColumnHeadersDefaultCellStyle.Font ?? Font).GetHeight(dpi))
                + ColumnHeadersDefaultCellStyle.Padding.Vertical;
            foreach (DataGridViewColumn column in Columns)
                if (column.Visible)
                {
                    DataGridViewCellStyle style = column.HeaderCell.InheritedStyle;
                    height = Math.Max(height, (int)Math.Ceiling((style.Font ?? Font).GetHeight(dpi)) + style.Padding.Vertical);
                }
            return Math.Max(4, height + ScaleWidth(HeaderHeightPadding, LogicalDpi, dpi, 1));
        }

        private void ScheduleStyleMetrics()
        {
            // 同一轮多处样式调整只测量一次，避免在框架样式事件中重入布局。
            if (disposed || !IsHandleCreated || styleMetricsPending || updatingStyleMetrics) return;
            styleMetricsPending = true;
            BeginInvoke(new Action(() => {
                if (!styleMetricsPending) return;
                styleMetricsPending = false;
                UpdateStyleMetrics();
            }));
        }

        private void UpdateStyleMetrics()
        {
            if (disposed || updatingStyleMetrics) return;
            updatingStyleMetrics = true;
            try { UpdateRowHeights(); AutoFitColumnHeaders(); }
            finally { updatingStyleMetrics = false; }
        }

        private int ComputeVisibleRowHeight(DataGridViewRow row, int baseHeight, Dictionary<Font, int> fontHeights)
        {
            int height = Math.Max(baseHeight, row.MinimumHeight);
            int dpi = DeviceDpi > 0 ? DeviceDpi : LogicalDpi;
            bool customRow = row.HasDefaultCellStyle && (row.DefaultCellStyle.Font != null || row.DefaultCellStyle.Padding != Padding.Empty);
            foreach (DataGridViewColumn column in Columns)
                if (column.Visible)
                {
                    // 通常全表共用列/行默认样式，模板高度已覆盖；仅自定义行/单元格需要完整继承样式。
                    if (!customRow && !row.Cells[column.Index].HasStyle) continue;
                    DataGridViewCellStyle style = row.Cells[column.Index].InheritedStyle;
                    height = Math.Max(height, MeasureFontHeight(style.Font ?? Font, dpi, fontHeights)
                        + style.Padding.Vertical + ScaleWidth(RowHeightPadding, LogicalDpi, dpi, 1));
                }
            return height;
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
            if (disposed || updatingRowHeights || !IsHandleCreated || Rows.Count == 0)
            {
                return;
            }
            updatingRowHeights = true;
            var fontHeights = new Dictionary<Font, int>();
            SuspendLayout();
            try
            {
                // 缩小行高会露出更多行；反复计算范围，直到可见区稳定。
                for (int pass = 0; pass <= Rows.Count; pass++)
                {
                    int first;
                    int count;
                    if (!TryGetVisibleRowRange(out first, out count))
                    {
                        break;
                    }
                    bool changed = false;
                    int last = Math.Min(Rows.Count, first + count);
                    for (int i = first; i < last; i++)
                    {
                        int target = ComputeVisibleRowHeight(Rows[i], height, fontHeights);
                        int previous = Rows[i].Height;
                        if (previous != target)
                        {
                            Rows[i].Height = target;
                            changed |= Rows[i].Height != previous;
                        }
                    }
                    if (!changed)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ResumeLayout();
                updatingRowHeights = false;
            }
        }

        private void OnScrollUpdateRowHeights(object sender, ScrollEventArgs e)
        {
            if (e.ScrollOrientation == ScrollOrientation.VerticalScroll)
                UpdateVisibleRowHeights(ComputeRowHeight());
        }

        private void OnResizeUpdateRowHeights(object sender, EventArgs e)
        {
            UpdateVisibleRowHeights(ComputeRowHeight());
        }

        private static void WriteAllTextAtomic(string path, string content, bool overwrite = true)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                if (overwrite && File.Exists(fullPath))
                {
                    // 替换不受支持或被锁定时报告失败，不能降级为会截断原文件的复制。
                    File.Replace(tempPath, fullPath, fullPath + ".bak", true);
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
            public bool? Visible { get; set; }

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

