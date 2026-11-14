using System;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UniversalUtilities.Tests
{
    [TestClass]
    public class FollowupTests
    {
        private sealed class Grid : IndustrialDataGridView<object[]>
        {
            public Grid(params Type[] types) : base(ColumnsFor(types), Color.Black, Color.Blue)
            { BindingContext = new BindingContext(); }
            private static ColumnStruct[] ColumnsFor(Type[] types)
            {
                var columns = new ColumnStruct[types.Length];
                for (int i = 0; i < types.Length; i++) columns[i] = new ColumnStruct { columnName = "C" + i, columnType = types[i] };
                return columns;
            }
            protected override object[] GetRowValues(object[] item) { return item; }
        }
        private sealed class CountingTable : DataTable
        {
            public static int RowsCreated;
            public CountingTable() : base("Archive") { }
            protected override DataTable CreateInstance() { return new CountingTable(); }
            protected override DataRow NewRowFromBuilder(DataRowBuilder builder)
            { RowsCreated++; return base.NewRowFromBuilder(builder); }
        }

        [TestMethod]
        public void 行默认Padding_追加与插入行在首次绘制前足够高()
        {
            TestHelpers.RunSta(() => { using (var form = new Form { ClientSize = new Size(900, 600) }) using (var grid = new Grid(typeof(string))) {
                grid.ShowRows(new[] { new object[] { "A" } }); grid.Dock = DockStyle.Fill; form.Controls.Add(grid); form.Show();
                grid.RowsDefaultCellStyle.Padding = new Padding(0, 25, 0, 25);
                Application.DoEvents(); grid.AppendRow(new object[] { "B" });
                Assert.IsTrue(grid.Rows[1].Height >= grid.Font.GetHeight() + 50, "新增行高度 " + grid.Rows[1].Height);
                // 不等待合并样式消息，插入仍须使用最新样式。
                grid.AlternatingRowsDefaultCellStyle.Padding = new Padding(0, 40, 0, 40);
                grid.InsertRow(0, new object[] { "C" });
                Assert.IsTrue(grid.Rows[0].Height >= grid.Font.GetHeight() + 50);
                grid.AppendRow(new object[] { "D" });
                Assert.IsTrue(grid.Rows[3].Height >= grid.Font.GetHeight() + 80);
                form.Hide();
            }});
        }

        [TestMethod]
        public void 非数值类型等价值_遵循DataTable转换且不发送通知()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(bool), typeof(string), typeof(DateTime), typeof(Guid), typeof(TimeSpan), typeof(object))) {
                var date = new DateTime(2026, 11, 14); var id = Guid.NewGuid(); var span = TimeSpan.FromMinutes(5);
                grid.ShowRows(new[] { new object[] { true, "123", date, id, span, "123" } });
                int changed = 0; grid.Table.DefaultView.ListChanged += (s, e) => changed++;
                Assert.AreEqual(5, grid.ApplyUpdates(new[] { new CellUpdate(0, 0, "true"), new CellUpdate(0, 1, 123),
                    new CellUpdate(0, 2, "2026-11-14"), new CellUpdate(0, 3, id.ToString()), new CellUpdate(0, 4, "00:05:00") }));
                Assert.AreEqual(0, changed);
                grid.SetCell(0, 5, 123); Assert.AreEqual(1, changed); Assert.AreEqual(typeof(int), grid.Table.Rows[0][5].GetType());
                Assert.IsFalse(grid.TrySetCell(0, 0, "NotBoolean")); Assert.AreEqual(1L, grid.FailedCellUpdateCount);
            }});
        }

        [TestMethod]
        public void 外部等数量替换行_选中读取必须明确报错()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(string))) {
                grid.ShowRows(new[] { new object[] { "A" } });
                grid.Table.Rows.Clear(); grid.Table.Rows.Add("B"); grid.Rows[0].Selected = true;
                Assert.ThrowsExactly<InvalidOperationException>(() => grid.GetSelectedItems());
                grid.ShowRows(new[] { new object[] { "C" } }); grid.Rows[0].Selected = true;
                Assert.AreEqual("C", grid.GetSelectedItems()[0][0]);
            }});
        }

        [TestMethod]
        public void 全量重建_不复制无关归档表的任何数据行()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(string))) using (var ds = new DataSet()) {
                grid.ShowRows(new[] { new object[] { "A" } }); ds.Tables.Add(grid.Table);
                var archive = new CountingTable(); archive.Columns.Add("Log", typeof(string)); ds.Tables.Add(archive);
                for (int i = 0; i < 20000; i++) archive.Rows.Add(new string('x', 256));
                CountingTable.RowsCreated = 0;
                grid.ShowRows(new[] { new object[] { "B" } });
                Assert.AreEqual(0, CountingTable.RowsCreated, "不能复制无关表数据"); Assert.AreEqual(20000, archive.Rows.Count);
            }});
        }

        [TestMethod]
        public void 预验证_仅有外键没有关系的传递关联仍被验证()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(string))) using (var ds = new DataSet()) {
                grid.Table.TableName = "Root"; grid.ShowRows(new[] { new object[] { "A" } }); ds.Tables.Add(grid.Table);
                var child = ds.Tables.Add("Child"); child.Columns.Add("Key", typeof(string)); child.Rows.Add("A");
                var grandchild = ds.Tables.Add("Grandchild"); grandchild.Columns.Add("Key", typeof(string)); grandchild.Rows.Add("A");
                child.Constraints.Add(new ForeignKeyConstraint(grid.Table.Columns[0], child.Columns[0]));
                grandchild.Constraints.Add(new ForeignKeyConstraint(child.Columns[0], grandchild.Columns[0]));
                Assert.AreEqual(0, ds.Relations.Count);
                TestHelpers.AssertThrows<ConstraintException>(() => grid.ShowRows(new[] { new object[] { "B" } }));
                Assert.AreEqual("A", grid.GetItemAt(0)[0]); Assert.IsTrue(ds.EnforceConstraints);
                grid.ShowRows(new[] { new object[] { "A" } }); Assert.IsTrue(ds.EnforceConstraints);
                ds.EnforceConstraints = false; grid.ShowRows(new[] { new object[] { "B" } });
                Assert.IsFalse(ds.EnforceConstraints);
            }});
        }

        [TestMethod]
        public void 预验证_无外键关系的表达式保留关联数据()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(string), typeof(int))) using (var ds = new DataSet()) {
                grid.Table.TableName = "Root"; grid.ShowRows(new[] { new object[] { "A", 0 } }); ds.Tables.Add(grid.Table);
                var parent = ds.Tables.Add("Parent"); parent.Columns.Add("Key", typeof(string)); parent.Columns.Add("Amount", typeof(int)); parent.Rows.Add("A", 7);
                ds.Relations.Add("Lookup", parent.Columns[0], grid.Table.Columns[0], false);
                grid.Table.Columns[1].Expression = "Parent(Lookup).Amount";
                grid.Table.Columns[1].AllowDBNull = false;
                grid.ShowRows(new[] { new object[] { "A", null } });
                Assert.AreEqual(7, grid.Table.Rows[0][1]);
                TestHelpers.AssertThrows<ConstraintException>(() => grid.ShowRows(new[] { new object[] { "Missing", null } }));
                Assert.AreEqual("A", grid.GetItemAt(0)[0]); Assert.IsTrue(ds.EnforceConstraints);
            }});
        }

        [TestMethod]
        public void 日期转换_保持列UTC模式且使用本地化解析()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(DateTime))) {
                grid.Table.Columns[0].DateTimeMode = DataSetDateTime.Utc;
                grid.Table.Locale = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                grid.ShowRows(new[] { new object[] { new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc) } });
                int changed = 0; grid.Table.DefaultView.ListChanged += (s, e) => changed++;
                grid.SetCell(0, 0, "14/11/2026"); Assert.AreEqual(0, changed);
                grid.SetCell(0, 0, "15/11/2026"); Assert.AreEqual(1, changed);
                Assert.AreEqual(DateTimeKind.Utc, ((DateTime)grid.Table.Rows[0][0]).Kind);
            }});
        }

        [TestMethod]
        public void 独立表跨表外键_拒绝未经保证的验证路径()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid(typeof(string))) using (var parent = new DataTable()) {
                grid.ShowRows(new[] { new object[] { "A" } });
                parent.Columns.Add("Key", typeof(string)); parent.PrimaryKey = new[] { parent.Columns[0] }; parent.Rows.Add("A");
                grid.Table.Constraints.Add(new ForeignKeyConstraint(parent.Columns[0], grid.Table.Columns[0]));
                var original = grid.Table.Rows[0];
                Assert.ThrowsExactly<InvalidOperationException>(() => grid.ShowRows(new[] { new object[] { "Unknown" } }));
                Assert.AreSame(original, grid.Table.Rows[0]);
            }});
        }

        [TestMethod]
        public void Demo列菜单_可逐列隐藏恢复且保护固定列()
        {
            TestHelpers.RunSta(() => {
                string dir = TestHelpers.CreateTempDirectory();
                try { using (var form = new IndustrialDataGridView.Demo.MainForm(System.IO.Path.Combine(dir, "layout.json"))) {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var grid = (IndustrialDataGridView.Demo.DemoGrid)form.GetType().GetField("grid", flags).GetValue(form);
                    grid.LayoutFile = System.IO.Path.Combine(dir, "layout.json"); form.Show();
                    var field = form.GetType().GetField("columnMenu", flags);
                    Assert.IsNotNull(field, "Demo 尚未提供列显隐菜单");
                    var menu = (ContextMenuStrip)field.GetValue(form); menu.Show(form, new Point(40, 60));
                    ToolStripMenuItem target = null;
                    foreach (ToolStripMenuItem item in menu.Items) {
                        if ((string)item.Tag == "Value") target = item;
                        if ((string)item.Tag == "Id" || (string)item.Tag == "Name") Assert.IsFalse(item.Enabled);
                    }
                    Assert.IsNotNull(target); Assert.IsTrue(target.Checked); target.PerformClick(); Assert.IsFalse(grid.Columns["Value"].Visible);
                    target.PerformClick(); Assert.IsTrue(grid.Columns["Value"].Visible); menu.Close(); form.Close();
                }} finally { TestHelpers.DeleteDirectory(dir); }
            });
        }
    }
}
