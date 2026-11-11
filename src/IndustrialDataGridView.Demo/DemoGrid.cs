using System;
using System.Collections.Generic;
using System.Drawing;
using UniversalUtilities;

namespace IndustrialDataGridView.Demo
{
    internal sealed class DemoRow
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

    internal sealed class DemoGrid : IndustrialDataGridView<DemoRow>
    {
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
