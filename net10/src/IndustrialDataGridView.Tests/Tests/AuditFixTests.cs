using System;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UniversalUtilities.Tests
{
    [TestClass]
    public class AuditFixTests
    {
        private sealed class Grid : IndustrialDataGridView<object[]>
        {
            public int FinalSelections;
            public Grid() : base(new[] {
                new ColumnStruct { columnName = "Name", columnType = typeof(string), sortable = true },
                new ColumnStruct { columnName = "Value", columnType = typeof(double), minimumWidth = 20 },
                new ColumnStruct { columnName = "Alarm", columnType = typeof(bool) }
            }, Color.Black, Color.Blue) { BindingContext = new BindingContext(); }
            protected override object[] GetRowValues(object[] item) { return item; }
            protected override void OnSelectionChanged(EventArgs e)
            {
                if (!IsUpdatingRows) FinalSelections++;
                base.OnSelectionChanged(e);
            }
            public DataGridViewCellStyle FormatCell(int row, int column)
            {
                var e = new DataGridViewCellFormattingEventArgs(column, row, Table.Rows[row][column],
                    typeof(string), Rows[row].Cells[column].InheritedStyle.Clone());
                OnCellFormatting(e);
                return e.CellStyle;
            }
        }
        private static object[] Row(string name, double value = 1) { return new object[] { name, value, false }; }
        private static void PumpUntil(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < 8000) { Application.DoEvents(); Thread.Sleep(5); }
            Assert.IsTrue(done(), "消息处理未达到预期状态");
        }
        private static void WithLayout(Action<Grid, string> action)
        {
            TestHelpers.RunSta(() => {
                string dir = TestHelpers.CreateTempDirectory();
                try { using (var grid = new Grid()) { grid.LayoutFile = Path.Combine(dir, "grid.json"); action(grid, dir); } }
                finally { TestHelpers.DeleteDirectory(dir); }
            });
        }
        private static DataSet AttachParent(Grid grid)
        {
            var ds = new DataSet();
            var parent = ds.Tables.Add("Parent");
            parent.Columns.Add("Name", typeof(string));
            parent.PrimaryKey = new[] { parent.Columns[0] };
            parent.Rows.Add("A"); parent.Rows.Add("B");
            grid.Table.TableName = "Child";
            ds.Tables.Add(grid.Table);
            ds.Relations.Add("Names", parent.Columns[0], grid.Table.Columns[0]);
            return ds;
        }

        [TestMethod]
        public void 外键重建失败_保留原行对象映射选中和约束()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                var item = Row("A"); grid.ShowRows(new[] { item });
                using (var ds = AttachParent(grid)) {
                    grid.Rows[0].Selected = true;
                    DataRow original = grid.Table.Rows[0];
                    TestHelpers.AssertThrows<ConstraintException>(() => grid.ShowRows(new[] { Row("Unknown") }));
                    Assert.AreSame(original, grid.Table.Rows[0]); Assert.AreSame(item, grid.GetItemAt(0));
                    Assert.AreSame(item, grid.GetSelectedItems()[0]); Assert.IsTrue(ds.EnforceConstraints);
                    grid.ShowRows(new[] { Row("B") });
                    Assert.AreEqual("B", grid.Table.Rows[0][0]); Assert.IsTrue(ds.EnforceConstraints);
                }
            }});
        }

        [TestMethod]
        public void 父表重建_拒绝悬空子行且允许保留引用的有效替换()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) using (var ds = new DataSet()) {
                var item = Row("A"); grid.ShowRows(new[] { item });
                grid.Table.TableName = "Parent"; ds.Tables.Add(grid.Table);
                var child = ds.Tables.Add("Child"); child.Columns.Add("Name", typeof(string)); child.Rows.Add("A");
                ds.Relations.Add("Names", grid.Table.Columns[0], child.Columns[0]);
                TestHelpers.AssertThrows<ConstraintException>(() => grid.ShowRows(new[] { Row("B") }));
                Assert.AreSame(item, grid.GetItemAt(0)); Assert.IsTrue(ds.EnforceConstraints);
                grid.ShowRows(new[] { Row("A", 2) });
                Assert.AreEqual(2.0, grid.Table.Rows[0][1]); Assert.AreEqual("A", child.Rows[0][0]);
                Assert.IsTrue(ds.EnforceConstraints);
            }});
        }

        [TestMethod]
        public void 只读列失败_同步批次继续且单条返回可诊断异常()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                grid.ShowRows(new[] { Row("A") }); grid.Table.Columns[0].ReadOnly = true;
                Assert.AreEqual(1, grid.ApplyUpdates(new[] { new CellUpdate(0, 0, "B"), new CellUpdate(0, 1, 2) }));
                Assert.IsFalse(grid.TrySetCell(0, 0, "C"));
                var ex = Assert.ThrowsExactly<ArgumentException>(() => grid.SetCell(0, 0, "D"));
                Assert.IsInstanceOfType<ReadOnlyException>(ex.InnerException);
                Assert.AreEqual(3L, grid.FailedCellUpdateCount); Assert.AreEqual(2.0, grid.Table.Rows[0][1]);
            }});
        }

        [TestMethod]
        public void 后台外键与只读失败_记账且继续消费()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                grid.ShowRows(new[] { Row("A") });
                using (var ds = AttachParent(grid)) {
                    grid.Table.Columns[2].ReadOnly = true; var handle = grid.Handle;
                    int escaped = 0;
                    ThreadExceptionEventHandler handler = (s, e) => escaped++;
                    Application.ThreadException += handler;
                    try {
                        var thread = new Thread(() => grid.PostUpdates(new[] {
                            new CellUpdate(0, 0, "Unknown"), new CellUpdate(0, 2, true), new CellUpdate(0, 1, 5) }));
                        thread.Start(); thread.Join();
                        PumpUntil(() => (double)grid.Table.Rows[0][1] == 5);
                        Assert.AreEqual(2L, grid.FailedCellUpdateCount); Assert.AreEqual(0, escaped);
                        Assert.IsNotNull(grid.LastCellUpdateError); Assert.IsTrue(ds.EnforceConstraints);
                    } finally { Application.ThreadException -= handler; }
                }
            }});
        }

        [TestMethod]
        public void 行替换_只通知受影响的选中且派生类收到最终状态()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                grid.ShowRows(new[] { Row("A"), Row("B") }); grid.ClearSelection(); grid.Rows[0].Selected = true;
                int events = 0; grid.SelectionChanged += (s, e) => events++;
                grid.FinalSelections = 0; grid.SetRow(1, Row("C"));
                Assert.AreEqual(0, events); Assert.AreEqual(0, grid.FinalSelections);
                var replacement = Row("D"); grid.SetRow(0, replacement);
                Assert.AreEqual(1, events); Assert.AreEqual(1, grid.FinalSelections);
                Assert.AreSame(replacement, grid.GetSelectedItems()[0]);
            }});
        }

        [TestMethod]
        public void 报警格式_保留普通单元格继承样式并拒绝错误列键()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                grid.ShowRows(new[] { Row("A") }); grid.AlarmColumnKey = "Alarm";
                grid.Columns[1].DefaultCellStyle.ForeColor = Color.Lime;
                grid.Rows[0].Cells[1].Style.BackColor = Color.DarkGreen;
                var style = grid.FormatCell(0, 1);
                Assert.AreEqual(Color.Lime, style.ForeColor); Assert.AreEqual(Color.DarkGreen, style.BackColor);
                Assert.ThrowsExactly<ArgumentException>(() => grid.AlarmColumnKey = "Alart");
                Assert.AreEqual("Alarm", grid.AlarmColumnKey);
                grid.SetCell(0, 2, true);
                Assert.AreEqual(grid.AlarmRowBackColor, grid.FormatCell(0, 1).BackColor);
            }});
        }

        [TestMethod]
        public void 零锁定列_第一列仍可见且其它列允许隐藏()
        {
            WithLayout((grid, dir) => {
                grid.LockedColumnCount = 0; grid.HideColumn("Name"); grid.HideColumn("Value"); grid.HideColumn("Alarm");
                grid.Columns[0].Visible = false;
                Assert.IsTrue(grid.Columns[0].Visible); Assert.IsFalse(grid.Columns[1].Visible);
                Assert.IsTrue(grid.SaveLayout()); Assert.IsTrue(grid.LoadLayout()); Assert.IsTrue(grid.Columns[0].Visible);
            });
        }

        [TestMethod]
        public void DPI布局回放_不恢复旧显隐且重算最小宽度()
        {
            WithLayout((grid, dir) => {
                Assert.IsTrue(grid.SaveLayout()); Assert.IsTrue(grid.LoadLayout()); grid.HideColumn("Value");
                var type = typeof(IndustrialDataGridView<object[]>);
                type.GetField("layoutWidthsChangedByUser", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, false);
                type.GetField("loadedLayoutDpi", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, 0);
                grid.Columns[1].MinimumWidth = 99;
                type.GetMethod("ReapplyLoadedLayoutIfDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(grid, null);
                Assert.IsFalse(grid.Columns[1].Visible); Assert.AreEqual(20, grid.Columns[1].MinimumWidth);
            });
        }

        [TestMethod]
        public void 显隐变更_自动保存()
        {
            WithLayout((grid, dir) => {
                var handle = grid.Handle; grid.HideColumn("Value");
                PumpUntil(() => File.Exists(grid.LayoutFile)); grid.ShowColumn("Value");
                Assert.IsTrue(grid.LoadLayout()); Assert.IsFalse(grid.Columns[1].Visible);
            });
        }

        [TestMethod]
        public void 布局损坏_从备份恢复并暴露警告()
        {
            WithLayout((grid, dir) => {
                grid.HideColumn("Value"); Assert.IsTrue(grid.SaveLayout());
                grid.ShowColumn("Value"); Assert.IsTrue(grid.SaveLayout());
                File.WriteAllText(grid.LayoutFile, "{bad json");
                Assert.IsTrue(grid.LoadLayout()); Assert.IsFalse(grid.Columns[1].Visible);
                Assert.IsNotNull(grid.LastLayoutWarning); Assert.IsNull(grid.LastLayoutError);
            });
        }

        [TestMethod]
        public void 保存文件被锁_原文件完整且自动重试成功()
        {
            WithLayout((grid, dir) => {
                var handle = grid.Handle; Assert.IsTrue(grid.SaveLayout());
                string original = File.ReadAllText(grid.LayoutFile);
                using (var locked = new FileStream(grid.LayoutFile, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    grid.HideColumn("Value"); PumpUntil(() => grid.LastLayoutError != null);
                    Assert.AreEqual(original, File.ReadAllText(grid.LayoutFile));
                }
                PumpUntil(() => grid.LastLayoutError == null);
                Assert.AreEqual(0, Directory.GetFiles(dir, "*.tmp").Length);
                Assert.IsTrue(grid.LoadLayout()); Assert.IsFalse(grid.Columns[1].Visible);
            });
        }

        [TestMethod]
        public void 迁移解析或备份失败_不改变当前布局和源文件()
        {
            WithLayout((grid, dir) => {
                grid.Columns[1].Width = 333; grid.HideColumn("Value");
                string widths = Path.Combine(dir, "width.json"); File.WriteAllText(widths, "{bad json");
                Assert.IsFalse(grid.MigrateLegacyLayout(widths, null));
                Assert.AreEqual(333, grid.Columns[1].Width); Assert.IsFalse(grid.Columns[1].Visible);
                File.WriteAllText(widths, "[{\"ID\":1,\"Width\":100}]"); Directory.CreateDirectory(widths + ".bak");
                Assert.IsFalse(grid.MigrateLegacyLayout(widths, null));
                Assert.AreEqual(333, grid.Columns[1].Width); Assert.IsFalse(grid.Columns[1].Visible); Assert.IsTrue(File.Exists(widths));
            });
        }

        [TestMethod]
        public void 跨数值类型等值写入_无通知且保留本地化转换()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                grid.ShowRows(new[] { Row("A", 1.5) }); grid.Table.Locale = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                int changed = 0; grid.Table.DefaultView.ListChanged += (s, e) => changed++;
                Assert.IsTrue(grid.TrySetCell(0, 1, 1.5m)); Assert.IsTrue(grid.TrySetCell(0, 1, "1,5"));
                Assert.AreEqual(0, changed); grid.SetCell(0, 1, (ushort)2); grid.SetCell(0, 1, 2);
                Assert.AreEqual(1, changed); Assert.AreEqual(2.0, grid.Table.Rows[0][1]);
            }});
        }

        [TestMethod]
        public void 字体与Padding_扩大可见行和表头且最小行高不重复改写()
        {
            TestHelpers.RunSta(() => { using (var font = new Font("Arial", 28)) using (var form = new Form()) using (var grid = new Grid()) {
                form.ClientSize = new Size(900, 500);
                grid.ShowRows(new[] { Row("A"), Row("B") }); grid.Dock = DockStyle.Fill; form.Controls.Add(grid); form.Show();
                grid.DefaultCellStyle.Font = font;
                grid.ColumnHeadersDefaultCellStyle.Font = font;
                grid.DefaultCellStyle.Padding = new Padding(0, 12, 0, 12);
                int oldWidth = grid.Columns[1].Width; grid.Columns[1].HeaderCell.Style.Padding = new Padding(80, 9, 80, 9);
                Application.DoEvents();
                Assert.IsTrue(grid.Rows[0].Height >= font.GetHeight() + 24, "行高 " + grid.Rows[0].Height + ", template " + grid.RowTemplate.Height + ", first " + grid.FirstDisplayedScrollingRowIndex + ", cell font " + grid.Rows[0].Cells[1].InheritedStyle.Font.Size + ", header " + grid.ColumnHeadersHeight);
                Assert.IsTrue(grid.ColumnHeadersHeight >= font.GetHeight() + 18, "表头高 " + grid.ColumnHeadersHeight);
                Assert.IsTrue(grid.Columns[1].Width >= oldWidth + 150, "列宽 " + oldWidth + " -> " + grid.Columns[1].Width);
                grid.Rows[0].MinimumHeight = 120; int changes = 0; grid.RowHeightChanged += (s, e) => changes++;
                grid.Width++; grid.Width--; Assert.AreEqual(0, changes);
                form.Hide();
            }});
        }

        [TestMethod]
        public void 稳定行句柄_投递前插行不串行且替换后丢弃()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) using (var other = new Grid()) {
                grid.ShowRows(new[] { Row("A"), Row("B") }); var row = grid.GetRowHandle(1);
                grid.InsertRow(0, Row("X"));
                var producer = new Thread(() => grid.PostCellUpdate(row, 1, 9)); producer.Start(); producer.Join();
                var handle = grid.Handle; PumpUntil(() => (double)grid.Table.Rows[2][1] == 9);
                Assert.AreEqual(1.0, grid.Table.Rows[1][1]);
                Assert.ThrowsExactly<ArgumentException>(() => other.PostCellUpdate(row, 1, 10));
                grid.SetRow(2, Row("C")); grid.PostCellUpdate(row, 1, 10);
                PumpUntil(() => grid.DroppedUpdateCount == 1); Assert.AreEqual(1.0, grid.Table.Rows[2][1]);
            }});
        }

        [TestMethod]
        public void 外部破坏绑定或行集合_明确拒绝错误映射()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) {
                grid.ShowRows(new[] { Row("A") }); grid.Table.Rows.Add(Row("B"));
                Assert.ThrowsExactly<InvalidOperationException>(() => grid.GetItemAt(0));
                grid.Table.Rows.RemoveAt(1); grid.DataSource = new DataTable();
                Assert.ThrowsExactly<InvalidOperationException>(() => grid.GetSelectedItems());
            }});
        }

        [TestMethod]
        public void 布局缺失显隐字段_默认显示并兼容未知列()
        {
            WithLayout((grid, dir) => {
                File.WriteAllText(grid.LayoutFile, "{\"version\":2,\"columns\":[{\"key\":\"Value\",\"width\":123},{\"key\":\"OldColumn\",\"visible\":false}]}");
                grid.HideColumn("Value"); Assert.IsTrue(grid.LoadLayout()); Assert.IsTrue(grid.Columns[1].Visible);
            });
        }

        [TestMethod]
        public void Demo窗口_报警排序刷新并定位新增行()
        {
            TestHelpers.RunSta(() => {
                string dir = TestHelpers.CreateTempDirectory();
                try { using (var form = new IndustrialDataGridView.Demo.MainForm(Path.Combine(dir, "demo.json"))) {
                    var type = form.GetType(); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var grid = (IndustrialDataGridView.Demo.DemoGrid)type.GetField("grid", flags).GetValue(form);
                    grid.LayoutFile = Path.Combine(dir, "demo.json"); form.Show();
                    ((System.Windows.Forms.Timer)type.GetField("timer", flags).GetValue(form)).Stop();
                    grid.Sort(grid.Columns["Alert"], ListSortDirection.Descending);
                    var tick = type.GetMethod("OnTick", flags);
                    for (int i = 0; i < 40; i++) tick.Invoke(form, new object[] { null, EventArgs.Empty });
                    foreach (int index in grid.GetVisibleDataRowIndexes()) {
                        var item = grid.GetItemAt(index);
                        Assert.AreEqual(item.Alert, grid.Table.Rows[index]["Alert"]);
                        Assert.AreEqual(item.Value, grid.Table.Rows[index]["Value"]);
                    }
                    Assert.AreEqual(0L, grid.FailedCellUpdateCount);
                    grid.Sort(grid.Columns["Id"], ListSortDirection.Descending);
                    ((Button)type.GetField("addButton", flags).GetValue(form)).PerformClick();
                    Assert.AreEqual(501, grid.Table.Rows.Count);
                    Assert.AreEqual(501, ((DataRowView)grid.Rows[grid.FirstDisplayedScrollingRowIndex].DataBoundItem)["Id"]);
                    form.Close(); Assert.IsTrue(File.Exists(grid.LayoutFile));
                }} finally { TestHelpers.DeleteDirectory(dir); }
            });
        }
    }
}
