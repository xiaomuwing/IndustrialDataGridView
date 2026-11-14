# IndustrialDataGridView .NET 10 版

这是主分支 `net10/` 目录中的 `IndustrialDataGridView<T>` .NET 10 移植工程，目标框架为 `net10.0-windows`。`.NET Framework 4.8` 版本位于仓库根目录。

## 项目结构

```text
IndustrialDataGridView.NET10.sln
src/
  IndustrialDataGridView/             控件类库
  IndustrialDataGridView.Demo/        Windows 窗体演示程序
  IndustrialDataGridView.Tests/       MSTest 测试
```

## 环境

- Windows
- .NET 10 SDK
- 可用的 .NET 10 Windows 桌面运行时
- NuGet 包：`Newtonsoft.Json` 13.0.4、`MSTest` 3.11.1

## 构建与测试

```powershell
dotnet build .\IndustrialDataGridView.NET10.sln -c Release

dotnet test .\IndustrialDataGridView.NET10.sln -c Release
```

当前 **73 项测试全部通过**。

与 .NET Framework 4.8 版本的刷新速度、处理器和内存占用对比见 [性能对比](PERFORMANCE.md)。

运行演示程序：

```powershell
dotnet run --project .\src\IndustrialDataGridView.Demo -c Release
```

## 移植说明

- 控件库目标框架改为 `net10.0-windows`，继续使用 Windows 窗体和 `Newtonsoft.Json` 布局序列化。
- 演示程序改用 `ApplicationConfiguration.Initialize()` 初始化，并使用 `PerMonitorV2` 每英寸点数模式。
- .NET 10 的 Windows 窗体设计器分析器会对若干公开属性报 `WFO1000`。本项目在控件库中暂时使用 `NoWarn` 保留原有属性行为；正式发布前建议补充设计器序列化元数据。
- .NET 10 的 `DataSet` 会在访问跨 `DataSet` 的 `ForeignKeyConstraint.RelatedTable` 时直接抛异常。控件库已把这种情况视为外部依赖处理，避免无关表阻断 `ShowRows`。
- .NET 10 在建立涉及外部外键的 `DataRelation` 时即会拒绝，因此相关边界测试改为验证平台限制；其余测试与 .NET Framework 版保持一致。
- 本项目不是本机提前编译工程；Windows 窗体当前不适合直接作为本机提前编译库使用。

## 平台

- 控件库：任意处理器架构；
- 演示程序和测试：64 位 Windows。

