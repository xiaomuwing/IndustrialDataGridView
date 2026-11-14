# IndustrialDataGridView

`IndustrialDataGridView<T>` 同时提供 **.NET Framework 4.8** 和 **.NET 10** 两个版本，包含：

- **IndustrialDataGridView**：类库，生成 `IndustrialDataGridView.dll`；
- **IndustrialDataGridView.Tests**：MSTest 测试（73 个用例，包含 49 个可靠性与审核修复回归用例）；
- **IndustrialDataGridView.Demo**：Windows 窗体演示程序。

**主分支同时包含两个版本。**

## 版本选择

| 版本 | 目标框架 | 解决方案 | 说明 |
|---|---|---|---|
| .NET Framework 4.8 | `v4.8` | `IndustrialDataGridView.sln` | 根目录版本，面向现有 .NET Framework 调用方 |
| .NET 10 | `net10.0-windows` | [`net10/IndustrialDataGridView.NET10.sln`](net10/IndustrialDataGridView.NET10.sln) | 独立移植工程，见 [`net10/README.md`](net10/README.md) |

两个版本当前各有 **73 项测试**。.NET 10 版本与 .NET Framework 版本的刷新速度、处理器和内存占用对比见 [net10/PERFORMANCE.md](net10/PERFORMANCE.md)。

## 环境

- Windows
- .NET Framework 4.8 Targeting Pack
- Visual Studio / Build Tools，包含 .NET Framework 4.8 开发工具和 C# 7.3 或更新编译器
- NuGet 包：Newtonsoft.Json 13.0.4、MSTest 3.11.1（首次还原需要网络或本机 NuGet 缓存）

## 目录

```
IndustrialDataGridView.sln
src/
  IndustrialDataGridView/
    IndustrialDataGridView.cs
    ColumnStruct.cs
    InternalLog.cs
    IndustrialDataGridView.csproj
  IndustrialDataGridView.Tests/
    Tests/IndustrialDataGridViewTests.cs
    Tests/TestHelpers.cs
    IndustrialDataGridView.Tests.csproj
  IndustrialDataGridView.Demo/
    DemoGrid.cs
    MainForm.cs
    Program.cs
    IndustrialDataGridView.Demo.csproj
packages/            # NuGet 还原目录，不提交
net10/
  IndustrialDataGridView.NET10.sln
  src/
  README.md
  PERFORMANCE.md
run-tests.ps1
```

## VS WinForms 设计器

`IndustrialDataGridView<T>` 是**泛型抽象基类**，WinForms 设计器不能直接实例化泛型/抽象控件，所以不能直接从工具箱拖它。正确做法是增加一个具体、非泛型子类：

```csharp
public sealed class ChannelGrid : IndustrialDataGridView<Channel>
{
    // 设计器需要公开无参构造
    public ChannelGrid()
        : base(CreateDefaultColumns(), Color.Black, Color.DarkBlue)
    {
    }

    private static IReadOnlyList<ColumnStruct> CreateDefaultColumns()
    {
        return new[]
        {
            new ColumnStruct { columnName = "Name", columnType = typeof(string), headerText = "通道", width = 120 },
            new ColumnStruct { columnName = "Value", columnType = typeof(double), headerText = "值", width = 100, format = "F3" }
        };
    }

    protected override object[] GetRowValues(Channel c)
    {
        return new object[] { c.Name, c.Value };
    }
}
```

步骤：

1. 把上述具体类放在窗体所在项目（或窗体引用的类库）中；
2. 确保类是 `public`、**有公开无参构造函数**，且构造函数里不要访问数据库/设备；
3. 重新生成项目；
4. 在工具箱中“选择项”加入该具体类，或直接把它拖到窗体上。

Demo 中的 `DemoGrid` 已改成 `public` 并提供无参构造，可作为模板。注意：设计器创建的是无参构造中的默认列；如果不同窗体需要不同列，建议在代码中创建控件，或为该场景再做一个具体适配类。

## 编译

用 Visual Studio 打开 `IndustrialDataGridView.sln`，或：

```powershell
msbuild IndustrialDataGridView.sln /t:Restore /p:Configuration=Debug
msbuild IndustrialDataGridView.sln /p:Configuration=Debug /t:Build
```

首次还原需要访问 NuGet；之后使用本机 NuGet 缓存。

产物：

```
src\IndustrialDataGridView\bin\Debug\IndustrialDataGridView.dll
src\IndustrialDataGridView.Tests\bin\Debug\IndustrialDataGridView.Tests.dll
src\IndustrialDataGridView.Demo\bin\Debug\IndustrialDataGridView.Demo.exe
```

Release 直接可用的类库在 `dist\`：

```
dist\IndustrialDataGridView.dll
dist\Newtonsoft.Json.dll
```

类库为 **AnyCPU**；Demo 和测试为 **x86**。如果宿主是 x86 工业程序，直接引用 DLL 即可。

## 测试

```powershell
pwsh -NoProfile -File .\run-tests.ps1
# 只跑某个用例
pwsh -NoProfile -File .\run-tests.ps1 -Filter "FullyQualifiedName~IndustrialDataGridViewTests"
```

当前 73 个用例全部通过。回归覆盖后台更新与增删/重建、外键与只读列异常、UI 消息响应、空值、锁定列换序、备份恢复与保存重试、迁移回滚、选中通知、字体/Padding、稳定行句柄，以及隔离布局路径的 Demo 窗口排序刷新和逐列显隐。审核历史文档已清理；当前验证结果以本文和测试结果为准。

脚本先通过 `vswhere` 查找已安装的最新工具，再回退到 PATH；也可指定 `-MSBuildPath` 和 `-VSTestPath`。遇到编译器服务器版本冲突时，加 `-DisableSharedCompilation`。本次验证使用该参数。

## Demo

```powershell
.\src\IndustrialDataGridView.Demo\bin\Debug\IndustrialDataGridView.Demo.exe
```

演示包含：

- 500 行初始数据，4 Hz 更新可见行；报警状态始终同步全表，保证报警排序使用当前状态；
- 运行时添加行、删除选中行；
- 点击 `Id` / `Name` / `Status` / `Group` 列头排序；
- 列对齐示例：`Id`、`Status`、`Unit`、`Group`、`Updated` 居中，`Name`、`Note` 左对齐，数值列右对齐；
- 拖动列头换序，布局自动保存到 `%LOCALAPPDATA%\IndustrialDataGridView.Demo\DemoGrid.layout.json`，下次启动恢复；首次运行兼容读取程序目录里的旧布局；
- 前两列锁定（冻结、始终可见、不可隐藏/换序）；
- 报警列为真时整行使用红色背景、白色前景（`AlarmColumnKey` / `AlarmRowBackColor` / `AlarmRowForeColor`）；选中报警行默认沿用普通选中色，选中状态不会被红色吞掉；
- 保存/加载布局、全部显示、字号 A+/A-/A0；
- **列显隐**按钮打开带勾选项的列菜单：取消勾选隐藏，重新勾选显示；前两列标为“固定”且不可取消，隐藏列也保留在菜单中；
- **Ctrl+↑ / Ctrl+↓ 调字号时自动加宽列头**，保证列头文字完整；自动加宽只影响显示，布局保存的是用户/默认基准宽度，字号恢复后列宽也恢复。
- 状态栏显示行数、可见行、选中数、应用耗时、字号。

## 控件要点

- 列在构造时固定；行上限 1000、列上限 256。
- 初始行集合用 `ShowRows` 建立；运行时用 `AppendRow` / `InsertRow` / `RemoveRow` 增删。
- `PostCellUpdate` 的行号在投递时解析为稳定目标；`PostUpdates` 整批使用同一份行快照。入队后的插入/删除不会让消息写到其它对象，目标删除、`SetRow` 替换或全量重建后的旧消息会丢弃，并计入 `DroppedUpdateCount`（该计数也包含队列满时的丢弃）。调用方仍需使用当前数据行号投递。
- 后台队列按每批最多 512 条、8 ms 检查预算消费，后续批次通过 UI 定时器调度，为输入和绘制留出消息处理机会；未变化的单元格值不会重复触发绑定通知。`ApplyUpdates` 的返回值仍包含成功接受的相同值更新。
- 所有数据写入入口将 `null` 统一处理为 `DBNull.Value`。`ShowRows` 先在隔离副本中验证类型与约束；存在 DataSet 时只导入通过外键或 DataRelation 连通的关联表数据，无关归档/日志表的行不复制。仍复制整个 DataSet 的结构以保留关系、表达式和命名空间；开销取决于结构大小及关联数据量。只在初始化/隐藏时重建，实时刷新使用增量接口。
- `GetSelectedItems()` / `SelectedItems` 返回当前选中的 `<T>` 列表。
- 行操作期间的 `SelectionChanged` 会合并到对象映射完成之后通知；选中行被 `SetRow` 替换后也会通知新的对象。
- `ColumnStruct.sortable = true` 的列允许点击列头排序。
- `AlarmColumnKey` 指定报警列；该列为真时整行背景为 `AlarmRowBackColor`（默认红色），前景为 `AlarmRowForeColor`（默认白色，与红底对比清晰）。可用 `IsAlarmRow(dataRowIndex)` 查询。
- 报警行选中时默认沿用控件普通选中背景/前景；如需“报警红 + 选中深红”的组合，可设置 `AlarmRowSelectedBackColor` / `AlarmRowSelectedForeColor`（默认 `Color.Empty`）。
- 字体变化时按当前字体测量列头文字，自动把列宽扩到可完整显示；列宽基准单独保存，自动加宽不污染布局。运行期修改 `HeaderText` 后可手动调用 `AutoFitColumnHeaders()`。
- `LockedColumnCount` 指定最左侧 N 列冻结（默认 1）。
- 锁定列之间的换序尝试在下一次 UI 消息中恢复，保存布局和关闭控件前也会校正；加载/重置布局时先解冻再排序，之后重新应用锁定状态。
- 布局文件为 v2：列宽按 96 DPI 逻辑像素保存，列顺序按显示顺序保存；加载时按 `DeviceDpi / 96` 缩放。
- 只刷新可见区的调用方在启用排序后应使用 `GetVisibleDataRowIndexes()` 把显示行映射为数据行。
- `ShowRows` / `ClearRows` 仍是全量重建，只能在控件不可见时调用。

### 列数量极限评估

控件的硬上限仍是 **256 列**（`MaxColumnCount`）和 **1000 行**（`MaxRowCount`），合计最多 256,000 个单元格。列数增加会总体增加全量重建、行高重算和逐格样式测量的开销，因此 256 列是“允许”的上限，不应直接理解为所有刷新频率下的推荐配置。

以下是审核用机、发布构建、1000 行、约 1100×700 像素窗体中的单次测量结果（3 次取中位数；“滚动”为程序化定位到第 500 行并处理消息）：

| 列数 | `ShowRows`（隐藏状态） | 滚动并处理消息 | 行高重算 | 窗体缩放 |
|---:|---:|---:|---:|---:|
| 14 | 17 毫秒 | 17 毫秒 | 小于 1 毫秒 | 3 毫秒 |
| 64 | 35 毫秒 | 29 毫秒 | 小于 1 毫秒 | 5 毫秒 |
| 128 | 67 毫秒 | 26 毫秒 | 小于 1 毫秒 | 5 毫秒 |
| 192 | 100 毫秒 | 27 毫秒 | 小于 1 毫秒 | 5 毫秒 |
| 256 | 105 毫秒 | 27 毫秒 | 小于 1 毫秒 | 5 毫秒 |

据此给出工程建议：

- **14 至 64 列**：单次全量重建和滚动均在 40 毫秒以内，适合常规实时列表；4 次/秒刷新仍应结合实际更新量在目标机器复测。
- **128 列**：全量重建接近 70 毫秒；增量刷新仍可用，但高频全量重建或极快滚动前应在目标机器复测。
- **192 至 256 列**：建议用于静态展示、低频刷新或初始化时重建；每次全量重建约 100 至 105 毫秒，滚动约 27 毫秒、缩放约 5 毫秒，现场需要按硬件、刷新率和操作频率验收。
- 上表是时间测量，不包含 1000×256 个界面单元格对象和 `DataTable` 数据的内存占用；大列数项目的内存上限仍需在目标机器验证。

这些数字是当前实现的审核用机参考值，不是跨硬件保证；改变行高样式、告警着色、每英寸点数或运行环境后应重新测量。实际项目建议把 **频繁变化的实时列表控制在 128 列以内**，把 **256 列保留给结构稳定、刷新频率较低的展示场景**。

## 审核修复后的接口约定

`TrySetCell` 对越界、类型转换、只读列和约束等可恢复数据错误返回 false 并计入 `FailedCellUpdateCount`；`ApplyUpdates` 和后台消费跳过失败项、继续后续项。`SetCell` 将数据写入失败包装为 `ArgumentException`，原异常保存在 `InnerException`。线程错误、外部破坏行映射及非数据层的宿主程序异常仍会抛出。

等值比较先按实际 DataColumn 存储规则转换，覆盖 bool、string、DateTime、Guid、TimeSpan、数值等支持的目标类型；使用 Table.Locale，并保留 DateTimeMode。转换在未绑定的临时行中完成，相等值不试写实际数据表，因此不发送多余的绑定通知；object 列保留原对象类型。

**外键验证要求父子表位于同一 DataSet，且 EnforceConstraints=true。** 控件不会替宿主打开原本关闭的约束。独立 DataTable 上的外键不在支持范围内；控件检测到当前表携带此类外键时会拒绝操作并给出配置错误，后台单元格写入计失败。独立父表无法枚举其它独立表对它的引用，不能依赖此检测代替正确的 DataSet 配置。受支持配置下的 ShowRows 预验证失败会保留原数据、映射、选中和约束开关。

动态增删表格时，后台生产者可以保存稳定句柄，避免投递前缓存的索引过期：

```csharp
// UI 线程：在确认当前数据行身份时取得句柄。
var row = grid.GetRowHandle(dataRowIndex);
int valueColumn = grid.GetColumnIndex("Value");
// 后台线程：期间插入其它行、删除其它行或排序均不改变目标。
grid.PostCellUpdate(row, valueColumn, latestValue);
```

删除、`SetRow` 替换或全量重建会使旧句柄失效，此后消息计入 `DroppedUpdateCount`。投递时已越界的索引是失败；有效目标入队后失效是丢弃，两者含义不同。没有逐批完成回调；索引版接口继续保留，调用者负责投递前的行号同步。

派生类重写 `OnSelectionChanged` 时，可通过 `IsUpdatingRows` 跳过行操作期间的中间虚调用；操作结束后会再调用重写方法，公开 `SelectionChanged` 也在映射一致后通知。未选中行替换不再无条件通知。

`AlarmColumnKey` 为空表示关闭报警；非空键必须存在，列类型必须是 bool、数值或 string，否则设置时抛 `ArgumentException` 并保留原配置。数值非零为报警；字符串使用 `bool.TryParse`，支持 True/False，任意业务状态名称需要宿主转换。普通单元格保留列、行和单元格继承样式；报警选中色仅在显式设置时覆盖。

`LockedColumnCount=0` 合法，但定义中的第一列仍始终可见。列宽、列顺序和显隐修改均去抖保存；自动保存失败后最多额外重试 3 次（1、2、4 秒），关闭时补写。显式 `SaveLayout` 失败返回 false，由调用者决定何时重试。保存使用唯一临时文件与原子替换，失败不会退化为非原子覆盖。加载主文件失败时尝试 `.bak`，成功恢复通过 `LastLayoutWarning` 提示，失败通过 `LastLayoutError` 提示。布局缺失 visible 字段按显示处理，未知列键继续忽略。

布局读写接口仍为 UI 线程同步操作，建议每个控件独占一个本地文件；网络盘或慢设备不保证 UI 延迟。Demo 使用用户本地目录，关闭保存失败会提示；窗口测试在构造时传入临时路径，不读取真实用户布局。成功保存清空 LastLayoutWarning。迁移先解析并备份旧文件，保留当前布局快照，应用成功后才发布新文件；应用或发布失败回滚界面及保存状态，旧文件保留。宿主事件持续抛异常导致回滚也失败时，将两次错误一起报告。旧文件清理失败单独记录警告。

预验证发现关联表的外键指向 DataSet 外部时明确报告配置错误；无关表的外部外键不阻断当前表。因 DataSet.Clone 也不能处理此配置，遇到无关外部外键时仅重建当前连通分量的验证结构，并恢复内部关系与表达式。

样式改变后的行高/表头重算合并到下一条 UI 消息；测量包含实际继承字体与 Padding。新行创建时立即按最新列、行、交替行和行模板样式初始化高度，不必等待滚动或下一轮消息；模板取奇偶行所需高度的最大值，避免裁剪。DPI 变化只重新计算宽度和最小宽度，不回放旧显隐/列序。Demo 已按 [Microsoft 的 .NET Framework 高 DPI 配置说明](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/high-dpi-support-in-windows-forms) 启用 PerMonitorV2；真实多显示器 DPI 切换尚未验收。

列结构和原 DataSource 必须保持固定，增删行须用控件 API；可增加数据约束、设置列 ReadOnly，以及使用 `Table.DefaultView.Sort` / `RowFilter` 实现多列排序和过滤。外部行集合变更会标记映射待检查，同数量换行也会在读取选中对象时明确报错，不再静默返回空集合；这仍不是任意 DataTable/Columns 修改的完整兼容层。允许零列，零列时 SaveLayout 返回 false；format 遵循类型格式化语义，字符串不会因此获得数值格式；非法 alignment 由 WinForms 校验。长文本可启用 `ShowCellToolTips` 或配置 WrapMode/框架自动行高，默认测量仍按单行文本设计。

Demo 的 250ms 定时器仍是 **4Hz 演示配置**，控件没有 4Hz 限制。生产刷新率需要结合实际行列数量和更新量验证。

## 依赖说明

`InternalLog.cs` 是独立解决方案中的日志空实现；原工程中的 `Logger` 未引入。
控件布局依赖 `Newtonsoft.Json`；Demo 和测试通过 `PackageReference` 还原，不提交 `packages/` 目录。
