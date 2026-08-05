# RelationProbe

SE2SW V3.2 的装配关系只读探针。它把"SE 到底能读出什么装配关系"从猜测变成实测表，
并顺带确认子装配整体导出 Parasolid 的可行性。**不是生产转换入口，不产出任何进入管线的文件。**

设计依据见 [`装配关系探针历史设计`](../../../b-Office/history/SE2SW/18-V3.2-装配关系探针.md)。

## 跑法

阶段 A（只读关系采集）：

```powershell
dotnet run --project .\tools\RelationProbe\RelationProbe.csproj -c Release -- `
  --samples "C:\OneHistory\OneHistory-Push\.claude\测试零件" > relation-probe.json
```

阶段 A + B（追加装配级导出与 SolidWorks 核验）：

```powershell
dotnet run --project .\tools\RelationProbe\RelationProbe.csproj -c Release -- `
  --samples "C:\OneHistory\OneHistory-Push\.claude\测试零件" `
  --export-check --sw-verify > relation-probe.json
```

| 参数 | 说明 |
|---|---|
| `--samples <目录>` | 必填。含 `.asm` 与 `.par` 的样件目录。 |
| `--asm <文件名>` | 可重复。默认探查目录下全部 `.asm`。 |
| `--work-dir <目录>` | 副本与导出产物的落位，默认 `%TEMP%\SE2SW-RelationProbe-<时间戳>`。 |
| `--in-place` | 直接读原件，不复制。仍做哈希前后比对。 |
| `--export-check` | 追加阶段 B：把每个 `.asm` 当独立顶层文档整体导出为 `.x_t`。 |
| `--sw-verify` | 阶段 B 用 SolidWorks 导入产物，核验实体数与坐标系。需要 `--export-check`。 |
| `--max-depth <n>` | COM 对象递归展开深度，默认 3（关系 → 几何元素 → 曲面）。 |

## 关键手法：先读类型库，再取值

既有探针用 `TryGet(() => obj.XXX, fallback)` 猜成员名。对 occurrence 够用，对关系不够用：
关系有十几种类型、每种成员不同，**猜漏一个不会抛异常，只会静默少采集**。

本探针先通过 `IDispatch::GetTypeInfo` 拿到 `ITypeInfo`，枚举 `GetFuncDesc` / `GetVarDesc` 并沿
继承链展开，得到真实成员名、种类（get/put/method）与参数个数，**再按这张表逐个取值**。
输出里的 `Members` 是"SE 2020 类型库里到底有什么"，`Values` 是"实际读到了什么"，
`Unreadable` 是"有这个成员但读不出来，HRESULT 是多少"——三者分开，不混为一谈。

V3.5 的生产代码应直接以 `Members` 为准，不再逐个 try/catch 试探。

## 只读保护

样件是生产资产，不是可再生 fixture。

- 默认把**整个样件目录**复制到工作目录再打开。只复制装配文件不行——`.asm` 内记的是绝对路径引用，
  只搬 `.asm` 会让引用指回原目录，等于没隔离。
- 无论是否 `--in-place`，都对样件目录做 SHA-256 全量前后比对；任一文件内容变化、消失或新增即判失败。
- 文档一律 `Close(false)`，从不保存。
- 导出产物落在 `<work-dir>\export\`，与样件副本分开；已存在同名产物时拒绝覆盖。
- Solid Edge / SolidWorks 的所有权规则与生产 Worker 一致：只有能证明实例是本进程新建的才退出它，
  附着到用户已开的会话时绝不退出、绝不改可见性。

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 关系采集成功，至少一个文档有关系 |
| 1 | 真故障：文档打不开、关系集合取不到、关系读不出接口类型、样件被改动、装配导出失败 |
| 2 | 用法错误 |
| 3 | **结论而非故障**：全部文档的关系集合都能正常打开且 `Count` 都是 0 |

退出码 3 与退出码 1 的区别是**普遍性**：集合能打开但普遍为 0，说明样件靠拖放定位、
没有显式关系，这类装配在 V3.5 里走固定，不阻塞 V3.2 的任何交付；集合打不开才是探针要修的故障。

体数与叶零件数不符**不判失败**——它是一条要写进实测报告的事实（例如隐藏件是否进入产物），
由生产门禁 `AssemblyProductionGate` 去卡，不由探针替它下结论。判定文案里会明确列出差异。

## 输出

单个 JSON 到标准输出，判定文案到标准错误。主要字段：

```
documents[]        每个 .asm 的采集结果
  relationEntryPoints   文档上名字含 Relation 的成员（来自类型库）
  relationCollection    关系集合自身的成员表
  relations[]           每条关系：接口名 + 成员表 + 实测值 + 几何元素递归展开
  occurrences[]         递归采集的实例与世界矩阵
exportChecks[]     装配级导出结果：生效的导出成员、FORMAT、体数、体包围盒中心、平均偏移
changedSampleFiles 样件变动清单，正常为空
verdict            判定文案
```

`exportChecks[].meanOffset` 是体包围盒中心均值减 occurrence 原点均值。两者不是同一个量，
不要求为 0；它的作用是**证伪**——若导出几何被整体搬到了别的坐标系，所有体会同向平移同一向量，
这个值会跟着整体跳变。落在零件尺度内即认为"几何位于子装配自身坐标系"成立。
