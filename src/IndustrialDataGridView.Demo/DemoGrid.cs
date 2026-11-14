using System;
using System.Collections.Generic;
using System.Drawing;
using UniversalUtilities;

namespace IndustrialDataGridView.Demo
{
    public sealed class DemoRow
    {
        public int Id;
        public string Name;
        public string Status;
        public double Value;
        public double Engineering;
        public double Max;
        public double Min;
        public double Average;
        public int Count;
        public bool Alert;
        public string Unit;
        public string Group;
        public DateTime Updated;
        public string Note;
    }

    /// <summary>演示用 14 列模型；ID/Name/Status/Group 可排序，前两列锁定。</summary>
    internal static class DemoRows
    {
        public static List<ColumnStruct> CreateColumns()
        {
            return new List<ColumnStruct>
            {
                new ColumnStruct { columnName = "Id", columnType = typeof(int), headerText = "通道号", width = 70, format = "F0", sortable = true },
                new ColumnStruct { columnName = "Name", columnType = typeof(string), headerText = "名称", width = 100, sortable = true },
                new ColumnStruct { columnName = "Status", columnType = typeof(string), headerText = "状态", width = 90, sortable = true },
                new ColumnStruct { columnName = "Value", columnType = typeof(double), headerText = "测量值", width = 100, format = "F3" },
                new ColumnStruct { columnName = "Engineering", columnType = typeof(double), headerText = "工程值", width = 100, format = "F3" },
                new ColumnStruct { columnName = "Max", columnType = typeof(double), headerText = "最大值", width = 100, format = "F3" },
                new ColumnStruct { columnName = "Min", columnType = typeof(double), headerText = "最小值", width = 100, format = "F3" },
                new ColumnStruct { columnName = "Average", columnType = typeof(double), headerText = "均值", width = 100, format = "F3" },
                new ColumnStruct { columnName = "Count", columnType = typeof(int), headerText = "采样数", width = 80, format = "F0" },
                new ColumnStruct { columnName = "Alert", columnType = typeof(bool), headerText = "报警", width = 60 , sortable = true},
                new ColumnStruct { columnName = "Unit", columnType = typeof(string), headerText = "单位", width = 65 },
                new ColumnStruct { columnName = "Group", columnType = typeof(string), headerText = "分组", width = 75, sortable = true },
                new ColumnStruct { columnName = "Updated", columnType = typeof(DateTime), headerText = "更新时间", width = 145, format = "yyyy-MM-dd HH:mm:ss" },
                new ColumnStruct { columnName = "Note", columnType = typeof(string), headerText = "备注", width = 180 }
            };
        }

        internal static List<CellUpdate> Simulate(List<DemoRow> rows, int[] visibleDataRows, int[] liveColumns, long cycle)
        {
            HashSet<int> visible = visibleDataRows == null ? null : new HashSet<int>(visibleDataRows);
            var updates = new List<CellUpdate>(rows.Count + (visible == null ? rows.Count : visibleDataRows.Length) * (liveColumns.Length - 1));

            for (int i = 0; i < rows.Count; i++)
            {
                DemoRow row = rows[i];
                double phase = cycle * 0.08 + i * 0.17;
                double raw = 100 + (i % 20) * 10 + 15 * Math.Sin(phase);
                double engineering = raw * 1.05 + 0.2;

                row.Value = raw;
                row.Engineering = engineering;
                if (raw > row.Max)
                {
                    row.Max = raw;
                }
                if (raw < row.Min)
                {
                    row.Min = raw;
                }
                row.Average = (row.Average * row.Count + raw) / (row.Count + 1);
                row.Count++;
                row.Alert = engineering > 160;
                row.Updated = DateTime.Now;

                // 报警参与排序，始终同步全表；其余采样值仅更新可见区。
                updates.Add(new CellUpdate(i, liveColumns[6], row.Alert));

                if (visible != null && !visible.Contains(i))
                {
                    continue;
                }

                updates.Add(new CellUpdate(i, liveColumns[0], row.Value));
                updates.Add(new CellUpdate(i, liveColumns[1], row.Engineering));
                updates.Add(new CellUpdate(i, liveColumns[2], row.Max));
                updates.Add(new CellUpdate(i, liveColumns[3], row.Min));
                updates.Add(new CellUpdate(i, liveColumns[4], row.Average));
                updates.Add(new CellUpdate(i, liveColumns[5], row.Count));
                updates.Add(new CellUpdate(i, liveColumns[7], row.Updated));
            }
            return updates;
        }

        public static DemoRow Create(int id)
        {
            double raw = 100 + (id % 20) * 10;
            double engineering = raw * 1.05 + 0.2;
            return new DemoRow
            {
                Id = id,
                Name = "CH" + id.ToString("D3"),
                Status = id % 23 == 0 ? "Error" : "Normal",
                Value = raw,
                Engineering = engineering,
                Max = raw,
                Min = raw,
                Average = raw,
                Count = 1,
                Alert = engineering > 160,
                Unit = id % 3 == 0 ? "με" : (id % 3 == 1 ? "kPa" : "mm"),
                Group = "G" + (id % 4 + 1),
                Updated = DateTime.Now,
                Note = "demo"
            };
        }

        public static List<DemoRow> CreateMany(int count)
        {
            var rows = new List<DemoRow>(count);
            for (int id = 1; id <= count; id++)
            {
                rows.Add(Create(id));
            }
            return rows;
        }
    }

    /// <summary>
    /// 具体、非泛型的子类示例。公开且带无参构造，WinForms 设计器才能实例化并放到窗体上；
    /// 泛型抽象基类本身不能直接拖入设计器。
    /// </summary>
    public sealed class DemoGrid : IndustrialDataGridView<DemoRow>
    {
        public DemoGrid()
            : this(DemoRows.CreateColumns(), Color.FromArgb(22, 22, 24), Color.FromArgb(0, 88, 140))
        {
        }

        public DemoGrid(IReadOnlyList<ColumnStruct> columns, Color back, Color select)
            : base(columns, back, select)
        {
        }

        protected override object[] GetRowValues(DemoRow row)
        {
            return new object[]
            {
                row.Id,
                row.Name,
                row.Status,
                row.Value,
                row.Engineering,
                row.Max,
                row.Min,
                row.Average,
                row.Count,
                row.Alert,
                row.Unit,
                row.Group,
                row.Updated,
                row.Note
            };
        }
    }
}
