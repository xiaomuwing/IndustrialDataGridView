using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UniversalUtilities.Tests
{
    [TestClass]
    public class ReviewBoundaryTests
    {
        private sealed class Grid : IndustrialDataGridView<object[]>
        {
            public Grid() : base(new[] {
                new ColumnStruct { columnName = "Key", columnType = typeof(string) },
                new ColumnStruct { columnName = "Value", columnType = typeof(int) }
            }, Color.Black, Color.Blue) { BindingContext = new BindingContext(); }
            protected override object[] GetRowValues(object[] item) { return item; }
        }
        private static void LayoutTest(Action<Grid, string> action)
        {
            TestHelpers.RunSta(() => {
                string dir = TestHelpers.CreateTempDirectory();
                try { using (var grid = new Grid()) { grid.LayoutFile = Path.Combine(dir, "layout.json"); action(grid, dir); } }
                finally { TestHelpers.DeleteDirectory(dir); }
            });
        }

        [TestMethod]
        public void 无关表引用外部父表_不阻断当前表重建()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) using (var ds = new DataSet { EnforceConstraints = false }) using (var parent = new DataTable("Outside")) {
                ds.Tables.Add(grid.Table); parent.Columns.Add("Id", typeof(string)); parent.Rows.Add("A");
                var other = new DataTable("Other"); other.Columns.Add("Id", typeof(string));
                other.Constraints.Add(new ForeignKeyConstraint(parent.Columns[0], other.Columns[0]));
                ds.Tables.Add(other);
                grid.ShowRows(new[] { new object[] { "A", 1 } });
                Assert.AreEqual("A", grid.GetItemAt(0)[0]); Assert.IsFalse(ds.EnforceConstraints);
            }});
        }

        [TestMethod]
        public void 关联表外部外键_平台在建立关系时拒绝()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) using (var ds = new DataSet { EnforceConstraints = false }) using (var parent = new DataTable("Outside")) {
                var original = new object[] { "A", 1 }; grid.ShowRows(new[] { original }); ds.Tables.Add(grid.Table);
                parent.Columns.Add("Id", typeof(string)); parent.Rows.Add("A");
                var child = new DataTable("Child"); child.Columns.Add("Id", typeof(string)); child.Rows.Add("A");
                child.Constraints.Add(new ForeignKeyConstraint(parent.Columns[0], child.Columns[0]));
                ds.Tables.Add(child);

                // .NET 10 在建立 DataRelation 时即拒绝跨 DataSet 的外键关系；
                // 因此“关联表携带外部外键”的控件配置错误分支无法在 .NET 10 正常构造。
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ds.Relations.Add("Link", grid.Table.Columns[0], child.Columns[0], false));
                StringAssert.Contains(ex.Message, "DataSets");
                Assert.AreSame(original, grid.GetItemAt(0));
            }});
        }

        [TestMethod]
        public void 跳过无关外部外键_仍恢复关联表达式和表内约束()
        {
            TestHelpers.RunSta(() => { using (var grid = new Grid()) using (var ds = new DataSet { EnforceConstraints = false }) using (var outside = new DataTable()) {
                ds.Tables.Add(grid.Table);
                var parent = ds.Tables.Add("Parent"); parent.Columns.Add("Id", typeof(string)); parent.Columns.Add("Amount", typeof(int)); parent.Rows.Add("A", 7);
                ds.Relations.Add("Lookup", parent.Columns[0], grid.Table.Columns[0], false);
                grid.Table.Columns[1].Expression = "Parent(Lookup).Amount"; grid.Table.Columns[0].Unique = true;
                outside.Columns.Add("Id", typeof(int));
                var unrelated = new DataTable("Unrelated"); unrelated.Columns.Add("Id", typeof(int));
                unrelated.Constraints.Add(new ForeignKeyConstraint(outside.Columns[0], unrelated.Columns[0])); ds.Tables.Add(unrelated);
                grid.ShowRows(new[] { new object[] { "A", null } });
                Assert.AreEqual(7, grid.Table.Rows[0][1]); Assert.IsTrue(grid.Table.Columns[0].Unique);
                Assert.AreEqual(1, unrelated.Constraints.Count); Assert.AreEqual(3, ds.Tables.Count);
            }});
        }

        [TestMethod]
        public void 备份恢复警告_成功保存后清空()
        {
            LayoutTest((grid, dir) => {
                grid.SaveLayout(); grid.SaveLayout(); File.WriteAllText(grid.LayoutFile, "{bad");
                Assert.IsTrue(grid.LoadLayout()); Assert.IsNotNull(grid.LastLayoutWarning);
                Assert.IsTrue(grid.SaveLayout()); Assert.IsNull(grid.LastLayoutWarning);
            });
        }

        [TestMethod]
        public void 迁移应用失败_恢复原布局且不发布新文件()
        {
            LayoutTest((grid, dir) => {
                grid.Columns[1].MinimumWidth = 33; grid.Columns[1].Width = 333; grid.HideColumn("Value");
                string widths = Path.Combine(dir, "width.json"); File.WriteAllText(widths, "[{\"ID\":1,\"Width\":100}]");
                bool failOnce = true;
                grid.ColumnWidthChanged += (s, e) => { if (failOnce) { failOnce = false; throw new InvalidOperationException("模拟应用布局失败"); } };
                Assert.IsFalse(grid.MigrateLegacyLayout(widths, null));
                Assert.AreEqual(333, grid.Columns[1].Width); Assert.IsFalse(grid.Columns[1].Visible);
                Assert.AreEqual(33, grid.Columns[1].MinimumWidth);
                Assert.IsFalse(File.Exists(grid.LayoutFile)); Assert.IsTrue(File.Exists(widths));
                StringAssert.Contains(grid.LastLayoutError, "模拟应用布局失败");
                Assert.IsTrue(grid.MigrateLegacyLayout(widths, null)); Assert.IsTrue(File.Exists(grid.LayoutFile));
            });
        }

        [TestMethod]
        public void 迁移发布失败_回滚显隐列宽且保留旧文件()
        {
            LayoutTest((grid, dir) => {
                grid.Columns[1].Width = 333; grid.HideColumn("Value");
                string widths = Path.Combine(dir, "width.json"); File.WriteAllText(widths, "[{\"ID\":1,\"Width\":100}]");
                Directory.CreateDirectory(grid.LayoutFile);
                Assert.IsFalse(grid.MigrateLegacyLayout(widths, null));
                Assert.AreEqual(333, grid.Columns[1].Width); Assert.IsFalse(grid.Columns[1].Visible); Assert.IsTrue(File.Exists(widths));
            });
        }

        [TestMethod]
        public void 行高优化_仍保留自定义单元格与行样式()
        {
            TestHelpers.RunSta(() => { using (var form = new Form { ClientSize = new Size(800, 600) }) using (var grid = new Grid()) using (var font = new Font("Arial", 30)) {
                grid.ShowRows(new[] { new object[] { "A", 1 }, new object[] { "B", 2 } }); form.Controls.Add(grid); grid.Dock = DockStyle.Fill; form.Show();
                grid.Rows[0].Cells[1].Style.Font = font; grid.Rows[0].Cells[1].Style.Padding = new Padding(0, 20, 0, 20);
                grid.Rows[1].DefaultCellStyle.Font = font; grid.Rows[1].DefaultCellStyle.Padding = new Padding(0, 30, 0, 30);
                Application.DoEvents();
                Assert.IsTrue(grid.Rows[0].Height >= font.GetHeight() + 40); Assert.IsTrue(grid.Rows[1].Height >= font.GetHeight() + 60);
                form.Hide();
            }});
        }

        [TestMethod]
        public void Demo构造_从指定临时路径加载布局()
        {
            TestHelpers.RunSta(() => {
                string dir = TestHelpers.CreateTempDirectory();
                try {
                    string path = Path.Combine(dir, "isolated.json");
                    File.WriteAllText(path, "{\"version\":2,\"columns\":[{\"key\":\"Id\",\"visible\":true},{\"key\":\"Name\",\"visible\":true},{\"key\":\"Value\",\"width\":120,\"visible\":false}]}");
                    using (var form = new IndustrialDataGridView.Demo.MainForm(path)) {
                        var field = form.GetType().GetField("grid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var grid = (IndustrialDataGridView.Demo.DemoGrid)field.GetValue(form);
                        Assert.AreEqual(path, grid.LayoutFile); Assert.IsFalse(grid.Columns["Value"].Visible);
                    }
                } finally { TestHelpers.DeleteDirectory(dir); }
            });
        }

        [TestMethod]
        public void Demo列_显式对齐配置应作用到实际列()
        {
            TestHelpers.RunSta(() => {
                using (var grid = new IndustrialDataGridView.Demo.DemoGrid()) {
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleCenter, grid.Columns["Id"].DefaultCellStyle.Alignment);
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleCenter, grid.Columns["Status"].DefaultCellStyle.Alignment);
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleRight, grid.Columns["Value"].DefaultCellStyle.Alignment);
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleCenter, grid.Columns["Updated"].DefaultCellStyle.Alignment);
                    Assert.AreEqual(DataGridViewContentAlignment.MiddleLeft, grid.Columns["Note"].DefaultCellStyle.Alignment);
                }
            });
        }
    }
}
