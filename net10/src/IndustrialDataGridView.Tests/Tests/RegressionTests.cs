using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UniversalUtilities;
using IndustrialDataGridView.Demo;

namespace UniversalUtilities.Tests
{
    [TestClass]
    public class RegressionTests
    {
        private sealed class Grid : IndustrialDataGridView<object[]>
        {
            public Grid() : base(new[]
            {
                new ColumnStruct { columnName = "Name", columnType = typeof(string), sortable = true },
                new ColumnStruct { columnName = "Value", columnType = typeof(double) },
                new ColumnStruct { columnName = "Alarm", columnType = typeof(bool), sortable = true }
            }, Color.Black, Color.Blue)
            {
                BindingContext = new BindingContext();
            }

            protected override object[] GetRowValues(object[] item) { return item; }
        }

        private static object[] Row(string name, object value = null)
        {
            return new object[] { name, value ?? 1.0, false };
        }

        private static void PumpUntil(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < 10000)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            Assert.IsTrue(done(), "UI 消息处理后未达到期望状态");
        }

        private static string[] Order(Grid grid)
        {
            return grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToArray();
        }

        [TestMethod]
        public void 排队更新_插入删除和排序后仍写入原对象()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.ShowRows(new[] { Row("A"), Row("B"), Row("C") });
                    var handle = grid.Handle;
                    var producer = new Thread(() => grid.PostCellUpdate(new CellUpdate(1, 1, 99.0)));
                    producer.Start();
                    producer.Join();
                    grid.RemoveRow(0);
                    grid.InsertRow(0, Row("X"));
                    grid.InsertRow(0, Row("Y"));
                    grid.Sort(grid.Columns["Name"], ListSortDirection.Descending);
                    PumpUntil(() => (double)grid.Table.Rows[2]["Value"] == 99.0);
                    Assert.AreEqual("B", grid.Table.Rows[2]["Name"]);
                    Assert.AreEqual(1.0, grid.Table.Rows[3]["Value"]);
                    Assert.AreEqual(0L, grid.FailedCellUpdateCount);
                }
            });
        }

        [TestMethod]
        public void 排队更新_删除替换重建和清空后旧消息不污染新行()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.ShowRows(new[] { Row("A") });
                    grid.PostCellUpdate(new CellUpdate(0, 1, 99.0));
                    grid.RemoveRow(0);
                    grid.AppendRow(Row("B"));
                    grid.PostCellUpdate(new CellUpdate(0, 1, 98.0));
                    grid.SetRow(0, Row("C"));
                    grid.PostCellUpdate(new CellUpdate(0, 1, 97.0));
                    grid.ShowRows(new[] { Row("D") });
                    grid.PostCellUpdate(new CellUpdate(0, 1, 96.0));
                    grid.ClearRows();
                    grid.AppendRow(Row("E"));
                    // 同时覆盖句柄创建前投递。
                    var handle = grid.Handle;
                    PumpUntil(() => grid.DroppedUpdateCount == 4);
                    Assert.AreEqual(1.0, grid.Table.Rows[0]["Value"]);
                    grid.PostCellUpdate(new CellUpdate(0, 1, 2.0));
                    PumpUntil(() => (double)grid.Table.Rows[0]["Value"] == 2.0);
                }
            });
        }

        [TestMethod]
        public void 排队更新_长队列消费期间允许其它UI回调执行()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.ShowRows(new[] { Row("A", -1.0) });
                    var handle = grid.Handle;
                    const int count = 10000;
                    for (int i = 0; i < count; i++)
                    {
                        grid.PostCellUpdate(new CellUpdate(0, 1, (double)i));
                    }
                    double valueAtOtherCallback = -2;
                    double valueAtTimerTick = -2;
                    grid.BeginInvoke(new Action(() => valueAtOtherCallback = (double)grid.Table.Rows[0][1]));
                    using (var heartbeat = new System.Windows.Forms.Timer { Interval = 1 })
                    {
                        heartbeat.Tick += (s, e) =>
                        {
                            if (valueAtTimerTick == -2) valueAtTimerTick = (double)grid.Table.Rows[0][1];
                        };
                        heartbeat.Start();
                        PumpUntil(() => (double)grid.Table.Rows[0][1] == count - 1);
                    }
                    Assert.IsTrue(valueAtOtherCallback >= -1 && valueAtOtherCallback < count - 1,
                        "不能等到整条队列消费完才处理其它 UI 工作");
                    Assert.IsTrue(valueAtTimerTick >= -1 && valueAtTimerTick < count - 1,
                        "消费队列时必须允许普通 Windows 定时消息运行，不能只让出 BeginInvoke 队列");
                    Assert.AreEqual(0L, grid.DroppedUpdateCount);
                    Assert.AreEqual(0L, grid.FailedCellUpdateCount);
                }
            });
        }

        [TestMethod]
        public void 相同值更新_不重复触发绑定通知且保持成功计数()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.ShowRows(new[] { Row("A") });
                    int notifications = 0;
                    grid.Table.DefaultView.ListChanged += (s, e) => notifications++;
                    var updates = Enumerable.Range(0, 10000).Select(i => new CellUpdate(0, 1, 1.0)).ToArray();
                    Assert.AreEqual(updates.Length, grid.ApplyUpdates(updates));
                    Assert.AreEqual(0, notifications);
                    grid.SetCell(0, 1, 2.0);
                    Assert.AreEqual(1, notifications);
                }
            });
        }

        [TestMethod]
        public void 空值_单元格整行和后台写入都能清除旧值()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.ShowRows(new[] { Row("A") });
                    Assert.IsTrue(grid.TrySetCell(0, "Value", null));
                    Assert.AreEqual(DBNull.Value, grid.Table.Rows[0]["Value"]);
                    grid.SetCell(0, "Alarm", null);
                    Assert.AreEqual(DBNull.Value, grid.Table.Rows[0]["Alarm"]);
                    grid.SetRow(0, new object[] { null, null, null });
                    Assert.IsTrue(grid.Table.Rows[0].ItemArray.All(v => v == DBNull.Value));
                    grid.SetCell(0, 1, 3.0);
                    var handle = grid.Handle;
                    grid.PostCellUpdate(new CellUpdate(0, 1, null));
                    PumpUntil(() => grid.Table.Rows[0].IsNull(1));
                    Assert.AreEqual(0L, grid.FailedCellUpdateCount);
                }
            });
        }

        [TestMethod]
        public void 锁定列_内部换序不会重入异常且消息完成后恢复()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.LockedColumnCount = 2;
                    grid.Columns[1].DisplayIndex = 0;
                    var handle = grid.Handle;
                    PumpUntil(() => grid.Columns[0].DisplayIndex == 0);
                    grid.Columns[1].DisplayIndex = 0;
                    PumpUntil(() => grid.Columns[0].DisplayIndex == 0);
                    CollectionAssert.AreEqual(new[] { "Name", "Value", "Alarm" }, Order(grid));
                    Assert.IsTrue(grid.Columns[0].Frozen && grid.Columns[1].Frozen);
                    Assert.IsFalse(grid.Columns[2].Frozen);
                }
            });
        }

        [TestMethod]
        public void 布局_锁定边界改变后仍可加载和重置()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string path = Path.Combine(dir, "layout.json");
                    using (var grid = new Grid())
                    {
                        grid.LayoutFile = path;
                        grid.Columns[2].DisplayIndex = 1;
                        Assert.IsTrue(grid.SaveLayout());
                        grid.LockedColumnCount = 2;
                        Assert.IsTrue(grid.ResetLayout());
                        CollectionAssert.AreEqual(new[] { "Name", "Value", "Alarm" }, Order(grid));
                        // 用当前冻结边界不同的历史文件验证加载。
                        File.WriteAllText(path, "{\"version\":2,\"dpi\":96,\"columns\":[{\"key\":\"Name\",\"visible\":true,\"width\":100},{\"key\":\"Alarm\",\"visible\":true,\"width\":100},{\"key\":\"Value\",\"visible\":true,\"width\":100}]}");
                        Assert.IsTrue(grid.LoadLayout(), grid.LastLayoutError);
                        CollectionAssert.AreEqual(new[] { "Name", "Alarm", "Value" }, Order(grid));
                        Assert.IsTrue(grid.Columns[2].Frozen);
                        Assert.IsFalse(grid.Columns[1].Frozen);
                        Assert.IsTrue(grid.ResetLayout());
                    }
                }
                finally { TestHelpers.DeleteDirectory(dir); }
            });
        }

        [TestMethod]
        public void 列换序后立即关闭_仍保存正确布局()
        {
            TestHelpers.RunSta(() =>
            {
                string dir = TestHelpers.CreateTempDirectory();
                try
                {
                    string path = Path.Combine(dir, "layout.json");
                    using (var grid = new Grid())
                    {
                        grid.LayoutFile = path;
                        grid.Columns[2].DisplayIndex = 1;
                    }
                    using (var restored = new Grid())
                    {
                        restored.LayoutFile = path;
                        Assert.IsTrue(restored.LoadLayout());
                        CollectionAssert.AreEqual(new[] { "Name", "Alarm", "Value" }, Order(restored));
                    }
                    using (var grid = new Grid())
                    {
                        grid.LayoutFile = path;
                        grid.LockedColumnCount = 2;
                        var handle = grid.Handle;
                        grid.Columns[1].DisplayIndex = 0;
                    }
                    using (var restored = new Grid())
                    {
                        restored.LayoutFile = path;
                        Assert.IsTrue(restored.LoadLayout());
                        CollectionAssert.AreEqual(new[] { "Name", "Value", "Alarm" }, Order(restored));
                    }
                }
                finally { TestHelpers.DeleteDirectory(dir); }
            });
        }

        [TestMethod]
        public void 全量加载_类型转换失败时保留原表对象映射和选中状态()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    object[] original = Row("A");
                    grid.ShowRows(new[] { original, Row("B") });
                    var handle = grid.Handle;
                    grid.ClearSelection();
                    grid.Rows[0].Selected = true;
                    DataTable table = grid.Table;
                    Assert.ThrowsExactly<ArgumentException>(() => grid.ShowRows(new[] { Row("X"), Row("Y", "bad") }));
                    Assert.AreSame(table, grid.DataSource);
                    Assert.AreEqual(2, grid.Table.Rows.Count);
                    Assert.AreSame(original, grid.GetItemAt(0));
                    Assert.AreSame(original, grid.GetSelectedItems().Single());
                    Assert.AreEqual("A", grid.Table.Rows[0][0]);
                }
            });
        }

        [TestMethod]
        public void 全量加载_约束失败时保留原数据()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    grid.ShowRows(new[] { Row("A"), Row("B") });
                    grid.Table.Columns["Name"].Unique = true;
                    Assert.ThrowsExactly<ConstraintException>(() => grid.ShowRows(new[] { Row("X"), Row("X") }));
                    Assert.AreEqual(2, grid.Table.Rows.Count);
                    Assert.AreEqual("A", grid.Table.Rows[0][0]);
                    grid.ShowRows(new[] { Row("X"), Row("Y") });
                    Assert.AreEqual("X", grid.Table.Rows[0][0]);
                }
            });
        }

        [TestMethod]
        public void 选中事件_删除和替换完成后通知最终对象()
        {
            TestHelpers.RunSta(() =>
            {
                using (var grid = new Grid())
                {
                    object[] second = Row("B");
                    grid.ShowRows(new[] { Row("A"), second, Row("C") });
                    var handle = grid.Handle;
                    grid.ClearSelection();
                    grid.Rows[0].Selected = true;
                    var observed = new List<object[][]>();
                    grid.SelectionChanged += (s, e) => observed.Add(grid.GetSelectedItems().ToArray());
                    grid.RemoveRow(0);
                    Assert.IsTrue(observed.Count > 0);
                    Assert.IsTrue(observed.All(items => items.Length == 1 && ReferenceEquals(items[0], second)));
                    object[] replacement = Row("NEW");
                    grid.SetRow(0, replacement);
                    Assert.AreSame(replacement, observed.Last().Single());
                    grid.ClearRows();
                    Assert.AreEqual(0, observed.Last().Length);
                }
            });
        }

        [TestMethod]
        public void 缩小字号_新露出的行立即应用当前高度()
        {
            TestHelpers.RunSta(() =>
            {
                using (var form = new Form { Width = 640, Height = 420, ShowInTaskbar = false, Opacity = 0 })
                using (var grid = new Grid { Dock = DockStyle.Fill })
                using (var largeFont = new Font("Arial", 60f))
                {
                    form.Controls.Add(grid);
                    grid.Font = largeFont;
                    grid.ShowRows(Enumerable.Range(0, 100).Select(i => Row("R" + i)).ToArray());
                    form.Show();
                    Application.DoEvents();
                    int oldHeight = grid.Rows[99].Height;
                    grid.ResetFontSize();
                    Application.DoEvents();
                    var visible = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Displayed).ToArray();
                    Assert.IsTrue(visible.Length > 6);
                    Assert.IsTrue(visible.All(r => r.Height == grid.RowTemplate.Height));
                    Assert.AreEqual(oldHeight, grid.Rows[99].Height, "屏外行应继续按需更新，避免全表布局开销");
                    form.Close();
                }
            });
        }

        [TestMethod]
        public void Demo只刷新可见区_屏外报警始终同步且排序正确()
        {
            TestHelpers.RunSta(() =>
            {
                var rows = DemoRows.CreateMany(500);
                using (var grid = new DemoGrid())
                {
                    grid.BindingContext = new BindingContext();
                    grid.ShowRows(rows);
                    int[] liveColumns = new[] { "Value", "Engineering", "Max", "Min", "Average", "Count", "Alert", "Updated" }
                        .Select(grid.GetColumnIndex).ToArray();
                    int[] visible = Enumerable.Range(0, 20).ToArray();
                    object originalHiddenValue = grid.Table.Rows[100]["Value"];
                    for (long cycle = 1; cycle <= 40; cycle++)
                    {
                        grid.ApplyUpdates(DemoRows.Simulate(rows, visible, liveColumns, cycle));
                    }
                    Assert.AreEqual(originalHiddenValue, grid.Table.Rows[100]["Value"]);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        Assert.AreEqual(rows[i].Alert, grid.Table.Rows[i]["Alert"], "报警状态必须与当前业务对象一致");
                    }
                    grid.Sort(grid.Columns["Alert"], ListSortDirection.Descending);
                    bool seenHealthy = false;
                    foreach (DataGridViewRow displayed in grid.Rows)
                    {
                        var view = (DataRowView)displayed.DataBoundItem;
                        bool alarm = rows[(int)view["Id"] - 1].Alert;
                        if (!alarm) seenHealthy = true;
                        else Assert.IsFalse(seenHealthy, "报警行应排在全部非报警行之前");
                    }
                }
            });
        }
    }
}
