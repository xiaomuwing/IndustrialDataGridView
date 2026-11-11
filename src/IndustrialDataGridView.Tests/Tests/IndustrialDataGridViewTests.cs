using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UniversalUtilities;

namespace UniversalUtilities.Tests
{
    /// <summary>
    /// IndustrialDataGridView.cs 覆盖。
    ///
    /// 约定：WinForms/GDI 用例统一用 TestHelpers.RunSta 包壳；
    /// 不依赖句柄的列/布局用例直接用不带窗体的控件，需要“可见时禁止重建”或跨线程的用例才创建窗体。
    /// </summary>
    [TestClass]
    public class IndustrialDataGridViewTests
    {
        public sealed class Row
        {
            public string Name { get; set; }
            public double Value { get; set; }
            public bool Alarm { get; set; }
        }

        private sealed class TestGrid : IndustrialDataGridView<Row>
        {
            public TestGrid(IReadOnlyList<ColumnStruct> columns)
                : base(columns, Color.Black, Color.DarkBlue)
            {
            }

            protected override object[] GetRowValues(Row item)
            {
                return new object[] { item.Name, item.Value, item.Alarm };
            }
        }

        private sealed class BadGrid : IndustrialDataGridView<Row>
        {
            public BadGrid(IReadOnlyList<ColumnStruct> columns)
                : base(columns, Color.Black, Color.DarkBlue)
            {
            }

            protected override object[] GetRowValues(Row item)
            {
                return new object[] { item.Name };
            }
        }

        private static List<ColumnStruct> Columns()
        {
            return new List<ColumnStruct>
            {
                new ColumnStruct { columnName = "Name", columnType = typeof(string), headerText = "通道", width = 160 },
                new ColumnStruct { columnName = "Value", columnType = typeof(double), headerText = "测量值(℃)", format = "F2", width = 140 },
                new ColumnStruct { columnName = "Alarm", columnType = typeof(bool), headerText = "报警", width = 60 }
            };
        }

        private static List<Row> Rows(int count)
        {
            var rows = new List<Row>(count);
            for (int i = 0; i < count; i++)
            {
                rows.Add(new Row { Name = "CH" + i, Value = i + 0.25, Alarm = (i % 3) == 0 });
            }
            return rows;
        }

        private static TestGrid CreateGrid()
        {
            var grid = new TestGrid(Columns());
            grid.BindingContext = new BindingContext();
            grid.DataSource = grid.Table;
            return grid;
        }

        private static List<ColumnStruct> SortableColumns()
        {
            List<ColumnStruct> columns = Columns();
            ColumnStruct name = columns[0];
            name.sortable = true;
            columns[0] = name;
            return columns;
        }

        private static List<string> DisplayOrder(IndustrialDataGridView<Row> grid)
        {
            var columns = new List<DataGridViewColumn>();
            foreach (DataGridViewColumn column in grid.Columns)
            {
                columns.Add(column);
            }
            columns.Sort((a, b) => a.DisplayIndex.CompareTo(b.DisplayIndex));
            var names = new List<string>(columns.Count);
            foreach (DataGridViewColumn column in columns)
            {
                names.Add(column.Name);
            }
            return names;
        }

        [TestMethod]
        public void 构造函数_应按列定义建立显式列并应用显示属性()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    Assert.AreEqual(3, grid.Columns.Count);
                    Assert.IsFalse(grid.AutoGenerateColumns);
                    Assert.AreEqual("通道", grid.Columns[0].HeaderText, "HeaderText 应独立于列名");
                    Assert.AreEqual("Name", grid.Columns[0].Name);
                    Assert.AreEqual("F2", grid.Columns[1].DefaultCellStyle.Format);
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleRight, grid.Columns[1].DefaultCellStyle.Alignment);
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleCenter, grid.Columns[2].DefaultCellStyle.Alignment);
                    Assert.AreEqual(DataGridViewColumnSortMode.NotSortable, grid.Columns[0].SortMode, "应禁用排序");
                    Assert.IsTrue(grid.ReadOnly);
                    Assert.IsFalse(grid.AllowUserToAddRows);
                    Assert.IsFalse(grid.AllowUserToDeleteRows);
                    Assert.AreEqual(160, grid.Columns[0].Width);
                    Assert.AreEqual(140, grid.Columns[1].Width);
                    Assert.AreEqual(3, grid.ColumnDefinitions.Count);
                    Assert.AreEqual(IndustrialDataGridView<Row>.MaxRowCount, 1000);
                    Assert.AreEqual(IndustrialDataGridView<Row>.MaxColumnCount, 256);
                }
            });
        }

        [TestMethod]
        public void 构造参数_应校验列数类型与重复列名()
        {
            TestHelpers.RunSta(() =>
            {
                Assert.ThrowsException<ArgumentNullException>(() => new TestGrid(null));

                var tooMany = new List<ColumnStruct>();
                for (int i = 0; i < 257; i++)
                {
                    tooMany.Add(new ColumnStruct { columnName = "C" + i, columnType = typeof(double) });
                }
                Assert.ThrowsException<ArgumentException>(() => new TestGrid(tooMany));

                Assert.ThrowsException<ArgumentException>(() => new TestGrid(new List<ColumnStruct>
                {
                    new ColumnStruct { columnName = "A", columnType = typeof(int?) }
                }));

                Assert.ThrowsException<ArgumentException>(() => new TestGrid(new List<ColumnStruct>
                {
                    new ColumnStruct { columnName = "A", columnType = null }
                }));

                Assert.ThrowsException<ArgumentException>(() => new TestGrid(new List<ColumnStruct>
                {
                    new ColumnStruct { columnName = "A", columnType = typeof(string) },
                    new ColumnStruct { columnName = "a", columnType = typeof(string) }
                }));
            });
        }

        [TestMethod]
        public void 行操作_应支持全量重建与单元格更新()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    grid.ShowRows(Rows(10));
                    Assert.AreEqual(10, grid.Table.Rows.Count);
                    Assert.AreEqual(0.25, Convert.ToDouble(grid.Table.Rows[0]["Value"]), 1e-9);

                    grid.SetRow(0, new Row { Name = "NEW", Value = 9.5, Alarm = true });
                    Assert.AreEqual("NEW", grid.Table.Rows[0]["Name"]);
                    Assert.AreEqual(9.5, Convert.ToDouble(grid.Table.Rows[0]["Value"]), 1e-9);

                    Assert.IsTrue(grid.TrySetCell(0, "Value", 3.25));
                    Assert.AreEqual(3.25, Convert.ToDouble(grid.Table.Rows[0]["Value"]), 1e-9);
                    Assert.IsFalse(grid.TrySetCell(0, "Value", "not-a-number"), "类型不匹配应返回 false");
                    Assert.IsTrue(grid.LastCellUpdateError != null);
                    Assert.IsTrue(grid.FailedCellUpdateCount >= 1);

                    var updates = new List<CellUpdate>
                    {
                        new CellUpdate(0, 0, "A"),
                        new CellUpdate(1, 1, 2.5),
                        new CellUpdate(9, 2, true)
                    };
                    Assert.AreEqual(3, grid.ApplyUpdates(updates));
                    Assert.AreEqual("A", (string)grid.Table.Rows[0]["Name"]);
                    Assert.AreEqual(2.5, Convert.ToDouble(grid.Table.Rows[1]["Value"]), 1e-9);
                    Assert.AreEqual(true, (bool)grid.Table.Rows[9]["Alarm"]);

                    grid.ClearRows();
                    Assert.AreEqual(0, grid.Table.Rows.Count);

                    grid.Columns[1].Width = 321;
                    grid.ShowRows(Rows(2));
                    Assert.AreEqual(321, grid.Columns[1].Width, "全量重建应保留显式列宽");
                    grid.ClearRows();
                }
            });
        }

        [TestMethod]
        public void 行操作_应支持运行时增加插入删除并保持对象映射()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    var original = Rows(3);
                    grid.ShowRows(original);

                    var appended = new Row { Name = "ADD", Value = 99, Alarm = true };
                    grid.AppendRow(appended);
                    Assert.AreEqual(4, grid.Table.Rows.Count);
                    Assert.AreSame(appended, grid.GetItemAt(3));

                    var inserted = new Row { Name = "INS", Value = 88, Alarm = false };
                    grid.InsertRow(1, inserted);
                    Assert.AreEqual(5, grid.Table.Rows.Count);
                    Assert.AreSame(inserted, grid.GetItemAt(1));
                    Assert.AreEqual("CH1", (string)grid.Table.Rows[2]["Name"], "插入后原有行应顺延");

                    grid.RemoveRow(0);
                    Assert.AreEqual(4, grid.Table.Rows.Count);
                    Assert.AreSame(inserted, grid.GetItemAt(0));
                    Assert.AreEqual("CH1", (string)grid.Table.Rows[1]["Name"]);

                    Assert.IsTrue(grid.RemoveRow(inserted));
                    Assert.AreEqual(3, grid.Table.Rows.Count);
                    Assert.IsFalse(grid.RemoveRow(inserted), "已删除对象再删应返回 false");

                    var replaced = new Row { Name = "REPLACED", Value = 7.5, Alarm = true };
                    grid.SetRow(0, replaced);
                    Assert.AreSame(replaced, grid.GetItemAt(0));
                    Assert.AreEqual("REPLACED", (string)grid.Table.Rows[0]["Name"]);

                    grid.ClearRows();
                    Assert.AreEqual(0, grid.Table.Rows.Count);
                    Assert.AreEqual(0, grid.GetSelectedItems().Count);
                }
            });
        }

        [TestMethod]
        public void 选中行_应返回对应的对象列表()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    var items = Rows(5);
                    grid.ShowRows(items);
                    grid.ClearSelection();

                    grid.Rows[1].Selected = true;
                    grid.Rows[3].Selected = true;
                    IReadOnlyList<Row> selected = grid.GetSelectedItems();
                    Assert.AreEqual(2, selected.Count);
                    Assert.AreSame(items[1], selected[0]);
                    Assert.AreSame(items[3], selected[1]);
                    Assert.AreEqual(2, grid.SelectedItems.Count);

                    grid.ClearSelection();
                    Assert.AreEqual(0, grid.GetSelectedItems().Count);

                    grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
                    grid.Rows[2].Cells[0].Selected = true;
                    grid.Rows[2].Cells[1].Selected = true;
                    grid.Rows[4].Cells[0].Selected = true;
                    selected = grid.GetSelectedItems();
                    Assert.AreEqual(2, selected.Count, "单元格选择模式应按行去重");
                    Assert.AreSame(items[2], selected[0]);
                    Assert.AreSame(items[4], selected[1]);
                }
            });
        }

        [TestMethod]
        public void 字号变化_应自动加宽列以显示完整列头()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string file = Path.Combine(dir, "font-width.json");
                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = file;
                        grid.Columns[0].HeaderText = "一个非常长的通道名称用于测试";
                        grid.Columns[0].Width = 240;
                        int baseWidth = grid.Columns[0].Width;

                        for (int i = 0; i < 20; i++)
                        {
                            grid.IncreaseFont();
                        }

                        Assert.IsTrue(grid.Columns[0].Width > baseWidth,
                            "字号增大后应至少加宽到能完整显示列头");

                        grid.ResetFontSize();
                        Assert.AreEqual(baseWidth, grid.Columns[0].Width,
                            "字号恢复默认后，自动加宽的列应回到基准宽度");

                        for (int i = 0; i < 20; i++)
                        {
                            grid.IncreaseFont();
                        }

                        Assert.IsTrue(grid.SaveLayout());
                        using (var loaded = CreateGrid())
                        {
                            loaded.LayoutFile = file;
                            Assert.IsTrue(loaded.LoadLayout());
                            Assert.AreEqual(baseWidth, loaded.Columns[0].Width,
                                "自动加宽只影响显示，不应把加宽后的宽度写进布局基准");
                        }
                    }
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 报警行_应按报警列状态识别并支持改色()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    grid.ShowRows(Rows(4));
                    Assert.IsFalse(grid.IsAlarmRow(0), "未设置报警列时不应判定报警");

                    grid.AlarmColumnKey = "Alarm";
                    Assert.AreEqual(Color.Red, grid.AlarmRowBackColor);
                    Assert.AreEqual(Color.White, grid.AlarmRowForeColor);
                    Assert.IsTrue(grid.AlarmRowSelectedBackColor.IsEmpty,
                        "默认选中背景应沿用普通选中色，不能与报警背景相同");
                    Assert.IsTrue(grid.AlarmRowSelectedForeColor.IsEmpty);
                    Assert.IsTrue(grid.IsAlarmRow(0));
                    Assert.IsFalse(grid.IsAlarmRow(1));
                    Assert.IsTrue(grid.IsAlarmRow(3));

                    Assert.IsTrue(grid.TrySetCell(0, "Alarm", false));
                    Assert.IsFalse(grid.IsAlarmRow(0), "报警清除后应恢复普通行");
                    Assert.IsTrue(grid.TrySetCell(1, "Alarm", true));
                    Assert.IsTrue(grid.IsAlarmRow(1), "报警打开后应进入报警状态");

                    grid.AlarmRowBackColor = Color.OrangeRed;
                    grid.AlarmRowForeColor = Color.Black;
                    grid.AlarmRowSelectedBackColor = Color.DarkRed;
                    grid.AlarmRowSelectedForeColor = Color.White;
                    Assert.AreEqual(Color.OrangeRed, grid.AlarmRowBackColor);
                    Assert.AreEqual(Color.Black, grid.AlarmRowForeColor);
                    Assert.AreEqual(Color.DarkRed, grid.AlarmRowSelectedBackColor);
                    Assert.AreEqual(Color.White, grid.AlarmRowSelectedForeColor);
                }
            });
        }

        [TestMethod]
        public void 行操作_应遵守1000行上限与GetRowValues长度约定()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    grid.ShowRows(Rows(1000));
                    Assert.AreEqual(1000, grid.Table.Rows.Count);
                    Assert.ThrowsException<ArgumentException>(() => grid.ShowRows(Rows(1001)));
                    Assert.ThrowsException<InvalidOperationException>(() => grid.AppendRow(new Row()));
                    Assert.AreEqual(1000, grid.Table.Rows.Count, "超限失败不应改动现有数据");
                }

                using (var bad = new BadGrid(Columns()))
                {
                    Assert.ThrowsException<InvalidOperationException>(() => bad.ShowRows(Rows(2)));
                    Assert.AreEqual(0, bad.Table.Rows.Count, "映射失败不应留下半套数据");
                    Assert.ThrowsException<InvalidOperationException>(() => bad.AppendRow(new Row()));
                }
            });
        }

        [TestMethod]
        public void 列显隐_第一列始终可见()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    Assert.IsTrue(grid.HideColumn("Value"));
                    Assert.IsFalse(grid.ShownInColumns("Value"));
                    Assert.IsFalse(grid.HideColumn("Name"), "第一列不允许隐藏");
                    Assert.IsTrue(grid.Columns[0].Visible);
                    Assert.IsFalse(grid.HideColumn("不存在"));
                    Assert.IsTrue(grid.ShowColumn("Value"));
                    Assert.IsTrue(grid.ShownInColumns("Value"));
                    grid.HideColumn("Value");
                    grid.ShowAllColumns();
                    Assert.IsTrue(grid.ShownInColumns("Value"), "ShowAllColumns 应恢复全部显示");
                    Assert.AreEqual(1, grid.GetColumnIndex("Value"));
                    Assert.AreEqual(-1, grid.GetColumnIndex("不存在"));
                }
            });
        }

        [TestMethod]
        public void 列排序_支持点击排序且选中对象仍正确()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new TestGrid(SortableColumns()))
                {
                    grid.BindingContext = new BindingContext();
                    grid.DataSource = grid.Table;
                    Assert.AreEqual(DataGridViewColumnSortMode.Automatic, grid.Columns[0].SortMode);

                    var items = Rows(4);
                    grid.ShowRows(items);
                    grid.ClearSelection();

                    grid.Sort(grid.Columns[0], ListSortDirection.Descending);
                    Assert.AreEqual("CH3", (string)grid.Rows[0].Cells[0].Value);
                    grid.Rows[0].Selected = true;
                    IReadOnlyList<Row> selected = grid.GetSelectedItems();
                    Assert.AreEqual(1, selected.Count);
                    Assert.AreSame(items[3], selected[0], "排序后选中行应映射回正确的对象");

                    grid.ClearSelection();
                    grid.Sort(grid.Columns[0], ListSortDirection.Ascending);
                    grid.Rows[0].Selected = true;
                    selected = grid.GetSelectedItems();
                    Assert.AreSame(items[0], selected[0]);
                }
            });
        }

        [TestMethod]
        public void 列顺序_保存后应按显示顺序恢复()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string file = Path.Combine(dir, "order.json");
                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = file;
                        grid.LockedColumnCount = 0;
                        grid.ShowRows(Rows(2));
                        grid.Columns[2].DisplayIndex = 0;
                        CollectionAssert.AreEqual(new[] { "Alarm", "Name", "Value" }, DisplayOrder(grid));
                        Assert.IsTrue(grid.SaveLayout());
                    }

                    using (var loaded = CreateGrid())
                    {
                        loaded.LayoutFile = file;
                        loaded.LockedColumnCount = 0;
                        Assert.IsTrue(loaded.LoadLayout());
                        CollectionAssert.AreEqual(new[] { "Alarm", "Name", "Value" }, DisplayOrder(loaded));
                    }
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 锁定列_前N列固定可见且不允许隐藏或换序()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    grid.LockedColumnCount = 2;
                    grid.ShowRows(Rows(2));

                    Assert.IsTrue(grid.Columns[0].Frozen);
                    Assert.IsTrue(grid.Columns[1].Frozen);
                    Assert.IsFalse(grid.Columns[2].Frozen);
                    Assert.IsTrue(grid.IsColumnLocked("Name"));
                    Assert.IsTrue(grid.IsColumnLocked("Value"));
                    Assert.IsFalse(grid.IsColumnLocked("Alarm"));

                    Assert.IsFalse(grid.HideColumn("Name"), "锁定列不允许隐藏");
                    Assert.IsFalse(grid.HideColumn("Value"), "锁定列不允许隐藏");
                    Assert.IsTrue(grid.HideColumn("Alarm"));
                    Assert.IsTrue(grid.ShowColumn("Alarm"));

                    grid.Columns[0].Visible = false;
                    Assert.IsTrue(grid.Columns[0].Visible, "直接隐藏锁定列也应被恢复");
                    Assert.IsTrue(grid.Columns[0].Frozen);

                    Assert.ThrowsException<InvalidOperationException>(
                        () => grid.Columns[2].DisplayIndex = 0,
                        "冻结列本身就会阻止未冻结列移到其前面");
                    CollectionAssert.AreEqual(new[] { "Name", "Value", "Alarm" }, DisplayOrder(grid));
                }
            });
        }

        [TestMethod]
        public void 布局_保存后应按列键原样读回并产生备份()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string file = Path.Combine(dir, "layout.json");
                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = file;
                        grid.HideColumn("Alarm");
                        grid.Columns[1].Width = 321;
                        Assert.IsTrue(grid.SaveLayout());
                        Assert.IsTrue(File.Exists(file));
                        StringAssert.Contains(File.ReadAllText(file), "\"version\": 2");
                        StringAssert.Contains(File.ReadAllText(file), "\"key\": \"Alarm\"");

                        grid.Columns[1].Width = 400;
                        Assert.IsTrue(grid.SaveLayout(), "第二次保存应成功");
                        Assert.IsTrue(File.Exists(file + ".bak"), "第二次保存应留下 .bak");
                        StringAssert.Contains(File.ReadAllText(file + ".bak"), "\"width\": 321");
                    }

                    using (var loaded = CreateGrid())
                    {
                        loaded.LayoutFile = file;
                        Assert.IsTrue(loaded.LoadLayout());
                        Assert.AreEqual(400, loaded.Columns[1].Width);
                        Assert.IsFalse(loaded.Columns[2].Visible);
                        Assert.IsTrue(loaded.Columns[0].Visible);
                    }

                    using (var reset = CreateGrid())
                    {
                        reset.LayoutFile = Path.Combine(dir, "reset.json");
                        reset.HideColumn("Value");
                        reset.Columns[0].Width = 400;
                        Assert.IsTrue(reset.ResetLayout());
                        Assert.AreEqual(160, reset.Columns[0].Width);
                        Assert.AreEqual(140, reset.Columns[1].Width);
                        Assert.IsTrue(reset.Columns[1].Visible);
                    }
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 布局_新版按96DPI逻辑宽度保存_旧版物理宽度按dpi换算()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string file = Path.Combine(dir, "logical.json");
                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = file;
                        grid.Columns[0].Width = 321;
                        Assert.IsTrue(grid.SaveLayout());
                        string json = File.ReadAllText(file);
                        StringAssert.Contains(json, "\"version\": 2");
                        StringAssert.Contains(json, "\"dpi\": 96");
                        StringAssert.Contains(json, "\"width\": 321");
                    }

                    // v1 旧文件：width 是保存时 DPI 下的物理像素，144 DPI 的 150px 在 96 DPI 下应为 100px
                    string legacy = Path.Combine(dir, "legacy-v1.json");
                    File.WriteAllText(legacy,
                        "{\"version\":1,\"dpi\":144,\"columns\":[{\"key\":\"Name\",\"visible\":true,\"width\":150}]}");
                    using (var loaded = CreateGrid())
                    {
                        loaded.LayoutFile = legacy;
                        Assert.IsTrue(loaded.LoadLayout());
                        Assert.AreEqual(100, loaded.Columns[0].Width);
                    }
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 布局_全隐藏强制第一列_损坏或高版本回退全部显示()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string allHidden = Path.Combine(dir, "all-hidden.json");
                    File.WriteAllText(allHidden,
                        "{\"version\":1,\"dpi\":96,\"columns\":[" +
                        "{\"key\":\"Name\",\"visible\":false,\"width\":160}," +
                        "{\"key\":\"Value\",\"visible\":false,\"width\":140}," +
                        "{\"key\":\"Alarm\",\"visible\":false,\"width\":60}]}");
                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = allHidden;
                        Assert.IsTrue(grid.LoadLayout());
                        Assert.IsTrue(grid.Columns[0].Visible, "全隐藏时必须强制第一列可见");
                        Assert.IsFalse(grid.Columns[1].Visible);
                        Assert.IsFalse(grid.Columns[2].Visible);
                    }

                    string broken = Path.Combine(dir, "broken.json");
                    File.WriteAllText(broken, "{{{ 不是 JSON");
                    using (var grid = CreateGrid())
                    {
                        grid.HideColumn("Value");
                        grid.LayoutFile = broken;
                        Assert.IsFalse(grid.LoadLayout());
                        Assert.IsTrue(grid.Columns[0].Visible);
                        Assert.IsTrue(grid.Columns[1].Visible, "损坏文件应回退全部显示");
                        Assert.IsTrue(grid.Columns[2].Visible);
                        Assert.IsTrue(grid.LastLayoutError != null);
                    }

                    string future = Path.Combine(dir, "future.json");
                    File.WriteAllText(future, "{\"version\":99,\"dpi\":96,\"columns\":[]}");
                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = future;
                        Assert.IsFalse(grid.LoadLayout());
                        Assert.IsTrue(grid.Columns[0].Visible && grid.Columns[1].Visible && grid.Columns[2].Visible);
                    }

                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = Path.Combine(dir, "missing.json");
                        grid.HideColumn("Value");
                        Assert.IsFalse(grid.LoadLayout());
                        Assert.IsTrue(grid.Columns[1].Visible);
                    }

                    using (var grid = CreateGrid())
                    {
                        Assert.IsFalse(grid.LoadLayout());
                        Assert.IsFalse(grid.SaveLayout());
                    }
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 布局_旧格式迁移应写新文件并备份旧文件()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string legacyWidth = Path.Combine(dir, "legacy-width.json");
                    string legacyShown = Path.Combine(dir, "legacy-shown.csv");
                    string newLayout = Path.Combine(dir, "migrated.json");
                    File.WriteAllText(legacyWidth, "[{\"ID\":0,\"width\":151},{\"ID\":1,\"width\":223},{\"ID\":2,\"width\":71}]");
                    File.WriteAllText(legacyShown, "1,0,1\r\n");

                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = newLayout;
                        Assert.IsTrue(grid.MigrateLegacyLayout(legacyWidth, legacyShown));
                        Assert.IsTrue(File.Exists(newLayout));
                        Assert.IsFalse(File.Exists(legacyWidth));
                        Assert.IsTrue(File.Exists(legacyWidth + ".bak"));
                        Assert.IsFalse(File.Exists(legacyShown));
                        Assert.IsTrue(File.Exists(legacyShown + ".bak"));
                        Assert.AreEqual(151, grid.Columns[0].Width);
                        Assert.AreEqual(223, grid.Columns[1].Width);
                        Assert.AreEqual(71, grid.Columns[2].Width);
                        Assert.IsTrue(grid.Columns[0].Visible);
                        Assert.IsFalse(grid.Columns[1].Visible);
                        Assert.IsTrue(grid.Columns[2].Visible);
                    }

                    using (var loaded = CreateGrid())
                    {
                        loaded.LayoutFile = newLayout;
                        Assert.IsTrue(loaded.LoadLayout());
                        Assert.IsFalse(loaded.Columns[1].Visible);
                        Assert.AreEqual(223, loaded.Columns[1].Width);
                    }

                    using (var grid = CreateGrid())
                    {
                        grid.LayoutFile = newLayout;
                        Assert.IsFalse(grid.MigrateLegacyLayout(legacyWidth + ".bak", legacyShown + ".bak"),
                            "新布局已存在时应拒绝迁移");
                    }
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 布局_列宽变化应在去抖后自动保存()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                var grid = CreateGrid();
                try
                {
                    string file = Path.Combine(dir, "auto.json");
                    grid.LayoutFile = file;
                    grid.Columns[0].Width = 222;

                    var watch = Stopwatch.StartNew();
                    while (!File.Exists(file) && watch.ElapsedMilliseconds < 5000)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    Assert.IsTrue(File.Exists(file), "停止拖动 500ms 后应自动保存布局");
                    StringAssert.Contains(File.ReadAllText(file), "\"width\": 222");
                }
                finally
                {
                    grid.Dispose();
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 布局_Dispose应补写未落盘的列宽()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string file = Path.Combine(dir, "dispose.json");
                    var grid = CreateGrid();
                    grid.LayoutFile = file;
                    grid.Columns[0].Width = 99;
                    grid.Dispose();

                    Assert.IsTrue(File.Exists(file), "Dispose 时应补写未落盘的布局");
                    StringAssert.Contains(File.ReadAllText(file), "\"width\": 99");
                }
                finally
                {
                    TestHelpers.DeleteDirectory(dir);
                }
            });
        }

        [TestMethod]
        public void 字号_应限制在6到72并保持常规字形()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    for (int i = 0; i < 200; i++)
                    {
                        grid.IncreaseFont();
                    }
                    Assert.AreEqual((double)IndustrialDataGridView<Row>.MaxFontSize, (double)grid.Font.SizeInPoints, 0.001);
                    Assert.AreEqual(FontStyle.Regular, grid.Font.Style);

                    for (int i = 0; i < 200; i++)
                    {
                        grid.DecreaseFont();
                    }
                    Assert.AreEqual((double)IndustrialDataGridView<Row>.MinFontSize, (double)grid.Font.SizeInPoints, 0.001);

                    grid.Font = new Font("微软雅黑", 12f, FontStyle.Bold);
                    grid.IncreaseFont();
                    Assert.AreEqual(FontStyle.Regular, grid.Font.Style, "放大后不应保留 Bold");
                    Assert.AreEqual(12.5f, grid.Font.SizeInPoints, 0.001f);

                    grid.ResetFontSize();
                    Assert.AreEqual((double)IndustrialDataGridView<Row>.DefaultFontSize, (double)grid.Font.SizeInPoints, 0.001);
                    Assert.IsTrue(grid.RowTemplate.Height > 0);
                }
            });
        }

        [TestMethod]
        public void 可见行范围_应返回当前显示区间()
        {
            TestHelpers.RunSta(() =>
            {
                using (var form = new Form { Width = 800, Height = 400, ShowInTaskbar = false })
                using (var grid = new TestGrid(Columns()) { Dock = DockStyle.Fill })
                {
                    form.Controls.Add(grid);
                    grid.ShowRows(Rows(100));
                    form.Show();
                    Application.DoEvents();

                    int first;
                    int count;
                    Assert.IsTrue(grid.TryGetVisibleRowRange(out first, out count));
                    Assert.IsTrue(count > 0 && count < 100, "可见行数应小于总行数");
                    Assert.IsTrue(grid.IsRowVisible(first));
                    Assert.IsFalse(grid.IsRowVisible(99), "末尾行不应在初始可见区");

                    grid.FirstDisplayedScrollingRowIndex = 50;
                    Application.DoEvents();
                    Assert.IsTrue(grid.TryGetVisibleRowRange(out first, out count));
                    Assert.IsTrue(first >= 49, "可见范围应包含上边缘余量");
                    Assert.IsTrue(grid.IsRowVisible(55));
                    form.Close();
                }
            });
        }

        [TestMethod]
        public void 独立控件即使有句柄_ShowRows也不应视为窗体可见()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = CreateGrid())
                {
                    IntPtr handle = grid.Handle;      // 强制创建句柄，但没有父窗体
                    Assert.IsTrue(grid.IsHandleCreated);
                    grid.ShowRows(Rows(2));           // 没有 FindForm 时不应触发“可见时禁止重建”
                    Assert.AreEqual(2, grid.Table.Rows.Count);
                }
            });
        }

        [TestMethod]
        public void 可见时_应允许运行时增删行但不允许全量重建()
        {
            TestHelpers.RunSta(() =>
            {
                using (var form = new Form { Width = 800, Height = 400, ShowInTaskbar = false })
                using (var grid = CreateGrid())
                {
                    form.Controls.Add(grid);
                    grid.Dock = DockStyle.Fill;
                    grid.ShowRows(Rows(2));
                    form.Show();
                    Application.DoEvents();

                    var appended = new Row { Name = "VISIBLE-ADD", Value = 1, Alarm = false };
                    grid.AppendRow(appended);
                    Assert.AreEqual(3, grid.Table.Rows.Count);
                    Assert.AreSame(appended, grid.GetItemAt(2));

                    var inserted = new Row { Name = "VISIBLE-INS", Value = 2, Alarm = true };
                    grid.InsertRow(0, inserted);
                    Assert.AreEqual(4, grid.Table.Rows.Count);
                    Assert.AreSame(inserted, grid.GetItemAt(0));

                    grid.RemoveRow(0);
                    Assert.AreEqual(3, grid.Table.Rows.Count);
                    Assert.AreSame(appended, grid.GetItemAt(2));
                    form.Close();
                }
            });
        }

        [TestMethod]
        public void 可见时_全量重建与清空应抛异常_隐藏后可执行()
        {
            TestHelpers.RunSta(() =>
            {
                using (var form = new Form { Width = 600, Height = 360, ShowInTaskbar = false })
                using (var grid = new TestGrid(Columns()) { Dock = DockStyle.Fill })
                {
                    form.Controls.Add(grid);
                    form.Show();
                    Application.DoEvents();
                    Assert.IsTrue(grid.IsHandleCreated);
                    Assert.IsTrue(grid.Visible);

                    Assert.ThrowsException<InvalidOperationException>(() => grid.ShowRows(Rows(2)));
                    Assert.ThrowsException<InvalidOperationException>(() => grid.ClearRows());

                    form.Hide();
                    Application.DoEvents();
                    grid.ShowRows(Rows(2));
                    Assert.AreEqual(2, grid.Table.Rows.Count);

                    form.Show();
                    Application.DoEvents();
                    Assert.AreEqual(2, grid.Table.Rows.Count);
                    form.Close();
                }
            });
        }

        [TestMethod]
        public void 线程_后台直接更新应抛异常_PostUpdate应生效()
        {
            TestHelpers.RunSta(() =>
            {
                using (var form = new Form { Width = 600, Height = 360, ShowInTaskbar = false })
                using (var grid = new TestGrid(Columns()) { Dock = DockStyle.Fill })
                {
                    form.Controls.Add(grid);
                    form.Show();
                    Application.DoEvents();
                    form.Hide();
                    Application.DoEvents();
                    grid.ShowRows(Rows(2));
                    form.Show();
                    Application.DoEvents();

                    Exception backgroundError = null;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            grid.SetCell(0, 1, 5.0);
                        }
                        catch (Exception ex)
                        {
                            backgroundError = ex;
                        }
                        grid.PostCellUpdate(new CellUpdate(0, 1, 6.5));
                    });
                    thread.Start();
                    thread.Join();
                    Assert.IsInstanceOfType(backgroundError, typeof(InvalidOperationException));

                    var watch = Stopwatch.StartNew();
                    while (Convert.ToDouble(grid.Table.Rows[0]["Value"]) != 6.5 && watch.ElapsedMilliseconds < 3000)
                    {
                        Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Assert.AreEqual(6.5, Convert.ToDouble(grid.Table.Rows[0]["Value"]), 1e-9, "后台排队更新应生效");
                    form.Close();
                }
            });
        }
    }
}

