# IndustrialDataGridView

`IndustrialDataGridView<T>` 的独立 .NET Framework 4.8 解决方案，包含：

- **IndustrialDataGridView**：类库，生成 `IndustrialDataGridView.dll`；
- **IndustrialDataGridView.Tests**：MSTest 测试（24 个用例）；
- **IndustrialDataGridView.Demo**：WinForms 演示程序。

## 环境

- Windows
- .NET Framework 4.8 Targeting Pack
- Visual Studio 2022/2026，或 MSBuild
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

当前 24 个用例全部通过。

## Demo

```powershell
.\src\IndustrialDataGridView.Demo\bin\Debug\IndustrialDataGridView.Demo.exe
```

演示包含：

- 500 行初始数据，10 Hz 只刷可见行；
- 运行时添加行、删除选中行；
- 点击 `Id` / `Name` / `Status` / `Group` 列头排序；
- 拖动列头换序，顺序自动保存到 `DemoGrid.layout.json`，下次启动恢复；
- 前两列锁定（冻结、始终可见、不可隐藏/换序）；
- 报警列为真时整行使用红色背景、白色前景（`AlarmColumnKey` / `AlarmRowBackColor` / `AlarmRowForeColor`）；选中报警行默认沿用普通选中色，选中状态不会被红色吞掉；
- 保存/加载布局、全部显示、字号 A+/A-/A0；
- **Ctrl+↑ / Ctrl+↓ 调字号时自动加宽列头**，保证列头文字完整；自动加宽只影响显示，布局保存的是用户/默认基准宽度，字号恢复后列宽也恢复。
- 状态栏显示行数、可见行、选中数、应用耗时、字号。

## 控件要点

- 列在构造时固定；行上限 1000、列上限 256。
- 初始行集合用 `ShowRows` 建立；运行时用 `AppendRow` / `InsertRow` / `RemoveRow` 增删。
- `GetSelectedItems()` / `SelectedItems` 返回当前选中的 `<T>` 列表。
- `ColumnStruct.sortable = true` 的列允许点击列头排序。
- `AlarmColumnKey` 指定报警列；该列为真时整行背景为 `AlarmRowBackColor`（默认红色），前景为 `AlarmRowForeColor`（默认白色，与红底对比清晰）。可用 `IsAlarmRow(dataRowIndex)` 查询。
- 报警行选中时默认沿用控件普通选中背景/前景；如需“报警红 + 选中深红”的组合，可设置 `AlarmRowSelectedBackColor` / `AlarmRowSelectedForeColor`（默认 `Color.Empty`）。
- 字体变化时按当前字体测量列头文字，自动把列宽扩到可完整显示；列宽基准单独保存，自动加宽不污染布局。运行期修改 `HeaderText` 后可手动调用 `AutoFitColumnHeaders()`。
- `LockedColumnCount` 指定最左侧 N 列冻结（默认 1）。
- 布局文件为 v2：列宽按 96 DPI 逻辑像素保存，列顺序按显示顺序保存；加载时按 `DeviceDpi / 96` 缩放。
- 只刷新可见区的调用方在启用排序后应使用 `GetVisibleDataRowIndexes()` 把显示行映射为数据行。
- `ShowRows` / `ClearRows` 仍是全量重建，只能在控件不可见时调用。

## 依赖说明

`InternalLog.cs` 是独立解决方案中的日志空实现；原工程中的 `Logger` 未引入。
控件布局依赖 `Newtonsoft.Json`；Demo 和测试通过 `PackageReference` 还原，不提交 `packages/` 目录。
