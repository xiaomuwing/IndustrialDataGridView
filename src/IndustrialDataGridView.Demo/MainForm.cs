using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using UniversalUtilities;

namespace IndustrialDataGridView.Demo
{
    /// <summary>
    /// .NET Framework 4.8 独立演示：
    /// 500 行初始化，10Hz 只刷可见行，支持运行时增删、选中列表、列拖拽换序、前两列锁定和部分列排序。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly List<DemoRow> rows = new List<DemoRow>();
        private readonly DemoGrid grid;
        private readonly Timer timer;
        private readonly int[] liveColumns;

        private Button pauseButton;
        private Button addButton;
        private Button deleteButton;
        private CheckBox visibleOnlyCheck;
        private ToolStripStatusLabel rowsLabel;
        private ToolStripStatusLabel visibleLabel;
        private ToolStripStatusLabel selectedLabel;
        private ToolStripStatusLabel applyLabel;
        private ToolStripStatusLabel fontLabel;
        private ToolStripStatusLabel messageLabel;

        private int[] visibleDataRows = new int[0];
        private int nextId;
        private long cycles;
        private double lastApplyMs;
        private bool running = true;

        public MainForm()
        {
            Text = "IndustrialDataGridView<DemoRow> — .NET Framework 4.8 Demo";
            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(900, 500);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(28, 28, 30);

            List<ColumnStruct> columns = DemoRows.CreateColumns();
            grid = new DemoGrid(columns, Color.FromArgb(64, 64, 64), Color.FromArgb(0, 88, 140))
            {
                Dock = DockStyle.Fill
            };
            grid.LayoutFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DemoGrid.layout.json");
            grid.LockedColumnCount = 2;
            grid.AlarmColumnKey = "Alert";
            grid.AlarmRowBackColor = Color.Red;
            grid.AlarmRowForeColor = Color.White;
            grid.LoadLayout();

            liveColumns = new[]
            {
                grid.GetColumnIndex("Value"),
                grid.GetColumnIndex("Engineering"),
                grid.GetColumnIndex("Max"),
                grid.GetColumnIndex("Min"),
                grid.GetColumnIndex("Average"),
                grid.GetColumnIndex("Count"),
                grid.GetColumnIndex("Alert"),
                grid.GetColumnIndex("Updated")
            };

            rows.AddRange(DemoRows.CreateMany(500));
            nextId = 501;
            grid.ShowRows(rows);
            BuildUi();

            grid.Scroll += delegate
            {
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
            };
            grid.Resize += delegate
            {
                UpdateVisibleDataRows();
                RefreshVisibleRows();
            };
            grid.SelectionChanged += delegate { UpdateStatus(); };
            grid.Sorted += delegate
            {
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
            };

            timer = new Timer { Interval = 1000 };
            timer.Tick += OnTick;
            Shown += delegate
            {
                timer.Start();
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
            };
            FormClosing += delegate
            {
                timer.Stop();
                grid.SaveLayout();
            };
        }

        private void BuildUi()
        {
            SuspendLayout();
            var toolPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(42, 42, 45),
                Padding = new Padding(8, 6, 8, 6)
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent
            };

            pauseButton = MakeButton("暂停", 66, delegate
            {
                running = !running;
                pauseButton.Text = running ? "暂停" : "启动";
                if (running)
                {
                    timer.Start();
                }
                else
                {
                    timer.Stop();
                }
                UpdateStatus();
            });
            flow.Controls.Add(pauseButton);

            addButton = MakeButton("添加行", 74, delegate
            {
                if (rows.Count >= IndustrialDataGridView<DemoRow>.MaxRowCount)
                {
                    SetMessage("已达到 1000 行上限");
                    return;
                }
                DemoRow row = DemoRows.Create(nextId++);
                grid.AppendRow(row);
                rows.Add(row);
                grid.FirstDisplayedScrollingRowIndex = grid.Rows.Count - 1;
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
                SetMessage("已添加 " + row.Name);
            });
            flow.Controls.Add(addButton);

            deleteButton = MakeButton("删除选中", 90, delegate
            {
                var indexes = new List<int>();
                foreach (DemoRow selected in grid.GetSelectedItems())
                {
                    int index = rows.IndexOf(selected);
                    if (index >= 0)
                    {
                        indexes.Add(index);
                    }
                }
                if (indexes.Count == 0)
                {
                    SetMessage("请先选中要删除的行");
                    return;
                }
                indexes.Sort();
                for (int i = indexes.Count - 1; i >= 0; i--)
                {
                    grid.RemoveRow(indexes[i]);
                    rows.RemoveAt(indexes[i]);
                }
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
                SetMessage("已删除 " + indexes.Count + " 行");
            });
            flow.Controls.Add(deleteButton);

            flow.Controls.Add(MakeButton("保存布局", 84, delegate
            {
                SetMessage(grid.SaveLayout() ? "布局已保存" : "布局保存失败: " + (grid.LastLayoutError ?? "未设置文件"));
            }));
            flow.Controls.Add(MakeButton("加载布局", 84, delegate
            {
                bool ok = grid.LoadLayout();
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
                SetMessage(ok ? "布局已加载" : "布局加载失败: " + (grid.LastLayoutError ?? "无文件"));
            }));
            flow.Controls.Add(MakeButton("全部显示", 84, delegate
            {
                grid.ShowAllColumns();
                UpdateStatus();
            }));
            flow.Controls.Add(MakeButton("A+", 44, delegate { grid.IncreaseFont(); UpdateStatus(); }));
            flow.Controls.Add(MakeButton("A-", 44, delegate { grid.DecreaseFont(); UpdateStatus(); }));
            flow.Controls.Add(MakeButton("A0", 44, delegate { grid.ResetFontSize(); UpdateStatus(); }));

            visibleOnlyCheck = new CheckBox
            {
                Text = "只刷可见行",
                Checked = true,
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Margin = new Padding(14, 8, 4, 0)
            };
            visibleOnlyCheck.CheckedChanged += delegate
            {
                UpdateVisibleDataRows();
                RefreshVisibleRows();
                UpdateStatus();
            };
            flow.Controls.Add(visibleOnlyCheck);

            toolPanel.Controls.Add(flow);

            var status = new StatusStrip
            {
                Dock = DockStyle.Bottom,
                SizingGrip = false,
                BackColor = Color.FromArgb(42, 42, 45),
                ForeColor = Color.Gainsboro
            };
            rowsLabel = new ToolStripStatusLabel();
            visibleLabel = new ToolStripStatusLabel();
            selectedLabel = new ToolStripStatusLabel();
            applyLabel = new ToolStripStatusLabel();
            fontLabel = new ToolStripStatusLabel();
            messageLabel = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGreen
            };
            status.Items.AddRange(new ToolStripItem[]
            {
                rowsLabel, visibleLabel, selectedLabel, applyLabel, fontLabel, messageLabel
            });

            Controls.Add(grid);
            Controls.Add(toolPanel);
            Controls.Add(status);
            ResumeLayout(true);

            UpdateStatus();
            SetMessage("拖动列头换序；点击 Id/Name/Status/Group 列头排序；前两列锁定");
        }

        private static Button MakeButton(string text, int width, EventHandler handler)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 64, 68),
                ForeColor = Color.White,
                Margin = new Padding(0, 2, 6, 0)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(110, 110, 115);
            button.Click += handler;
            return button;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!running)
            {
                return;
            }

            UpdateVisibleDataRows();
            var watch = Stopwatch.StartNew();
            List<CellUpdate> updates = Simulate();
            if (updates.Count > 0)
            {
                int applied = grid.ApplyUpdates(updates);
                watch.Stop();
                lastApplyMs = watch.Elapsed.TotalMilliseconds;
                if (applied != updates.Count)
                {
                    SetMessage("部分更新失败，最近错误: " + (grid.LastCellUpdateError ?? ""));
                }
            }
            else
            {
                watch.Stop();
                lastApplyMs = 0;
            }
            UpdateStatus();
        }

        private List<CellUpdate> Simulate()
        {
            long cycle = ++cycles;
            HashSet<int> visible = visibleDataRows == null ? null : new HashSet<int>(visibleDataRows);
            var updates = new List<CellUpdate>(visible == null ? rows.Count * liveColumns.Length : visibleDataRows.Length * liveColumns.Length);

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

                if (visible != null && !visible.Contains(i))
                {
                    continue;
                }

                Add(updates, i, liveColumns[0], row.Value);
                Add(updates, i, liveColumns[1], row.Engineering);
                Add(updates, i, liveColumns[2], row.Max);
                Add(updates, i, liveColumns[3], row.Min);
                Add(updates, i, liveColumns[4], row.Average);
                Add(updates, i, liveColumns[5], row.Count);
                Add(updates, i, liveColumns[6], row.Alert);
                Add(updates, i, liveColumns[7], row.Updated);
            }
            return updates;
        }

        private void UpdateVisibleDataRows()
        {
            if (!visibleOnlyCheck.Checked)
            {
                visibleDataRows = null;
                return;
            }

            IReadOnlyList<int> dataRows = grid.GetVisibleDataRowIndexes();
            var snapshot = new int[dataRows.Count];
            for (int i = 0; i < dataRows.Count; i++)
            {
                snapshot[i] = dataRows[i];
            }
            visibleDataRows = snapshot;
        }

        private void RefreshVisibleRows()
        {
            if (!visibleOnlyCheck.Checked || rows.Count == 0)
            {
                return;
            }

            IReadOnlyList<int> dataRows = grid.GetVisibleDataRowIndexes();
            var updates = new List<CellUpdate>(dataRows.Count * liveColumns.Length);
            for (int i = 0; i < dataRows.Count; i++)
            {
                int rowIndex = dataRows[i];
                if (rowIndex < 0 || rowIndex >= rows.Count)
                {
                    continue;
                }
                DemoRow row = rows[rowIndex];
                Add(updates, rowIndex, liveColumns[0], row.Value);
                Add(updates, rowIndex, liveColumns[1], row.Engineering);
                Add(updates, rowIndex, liveColumns[2], row.Max);
                Add(updates, rowIndex, liveColumns[3], row.Min);
                Add(updates, rowIndex, liveColumns[4], row.Average);
                Add(updates, rowIndex, liveColumns[5], row.Count);
                Add(updates, rowIndex, liveColumns[6], row.Alert);
                Add(updates, rowIndex, liveColumns[7], row.Updated);
            }
            if (updates.Count > 0)
            {
                grid.ApplyUpdates(updates);
            }
        }

        private static void Add(List<CellUpdate> updates, int rowIndex, int columnIndex, object value)
        {
            if (columnIndex >= 0)
            {
                updates.Add(new CellUpdate(rowIndex, columnIndex, value));
            }
        }

        private void UpdateStatus()
        {
            rowsLabel.Text = "行数: " + rows.Count + "/" + IndustrialDataGridView<DemoRow>.MaxRowCount;
            int selected = grid.GetSelectedItems().Count;
            selectedLabel.Text = "选中: " + selected;
            deleteButton.Enabled = selected > 0;
            addButton.Enabled = rows.Count < IndustrialDataGridView<DemoRow>.MaxRowCount;

            int first;
            int count;
            if (grid.TryGetVisibleRowRange(out first, out count))
            {
                visibleLabel.Text = visibleOnlyCheck.Checked
                    ? "可见行: " + count + " (" + first + ".." + (first + count - 1) + ")"
                    : "可见行: 全表";
            }
            else
            {
                visibleLabel.Text = "可见行: 0";
            }
            applyLabel.Text = "应用: " + lastApplyMs.ToString("F2") + " ms";
            fontLabel.Text = "字号: " + grid.Font.SizeInPoints.ToString("F1") + " pt";
        }

        private void SetMessage(string text)
        {
            if (messageLabel != null)
            {
                messageLabel.Text = text;
            }
        }
    }
}
