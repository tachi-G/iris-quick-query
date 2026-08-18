# IRIS 快捷查询

面向医院项目运维人员的 Windows 单机只读查询工具。应用将患者标识、登记号、内部 ID、就诊日期等信息抽象为全局业务元素，再通过参数化 SQL 规则描述元素之间的查询关系，让使用者从任意已知字段出发，逐步获得相关信息。

当前基准版本：`1.0.15`

> 本项目只负责安全地组织和执行只读查询。数据库账号仍必须由服务器端限制为只读，客户端校验不能替代数据库权限。

## 核心能力

- **元素化查询**：使用固定 ID 管理全局业务元素，支持人工输入、SQL 回填、列表选择和日期规范化。
- **依赖自动调度**：根据现有元素值运行输入已就绪的规则，支持级联查询、循环收敛和输入变化后的重新执行。
- **多结果交互**：单行结果自动选择，多行结果由使用者确认后继续查询。
- **冲突保护**：同一元素出现不同非空值时停止其下游规则，避免静默覆盖。
- **配置原子保存**：元素和 SQL 规则作为一份当前配置保存，快速重复操作不会并发写坏配置。
- **规则包导入导出**：支持带版本和 SHA-256 校验和的 `.irisqconfig` 规则包，并安全合并规则实际引用的元素。
- **安装版与绿色版**：均为 Windows x64 self-contained 构建，目标电脑无需另行安装 .NET Runtime。

## 安全与隐私

应用只允许执行以下 SQL：

- 参数化的单条 `SELECT`；
- 最终语句为 `SELECT` 的 CTE。

所有元素输入都会转换为 ODBC `?` 参数。应用拒绝 DML、DDL、`CALL` 和多语句 SQL，不会把患者输入直接拼接到 SQL 文本中。

患者输入、SQL 派生值、查询结果和列表选择只存在于运行内存，不写入 SQLite、日志或规则包。唯一例外是配置者主动填写的规则测试参数：这些值与数据库密码一样，使用当前 Windows 用户范围的 DPAPI 加密后保存在本地。

日志只记录规则 ID、状态、耗时、行数和错误类别，不记录 SQL 参数值或查询结果。

## 运行要求

- Windows 10/11 64 位；
- 由医院 IT 安装官方 64 位 InterSystems IRIS ODBC 驱动；
- 由数据库管理员提供权限受限的 IRIS 只读账号。

应用不捆绑数据库厂商驱动。新电脑或全新 Windows 用户首次运行时使用空白配置，不会预置全局元素、SQL 规则或连接信息。

## 快速开始

1. 安装官方 64 位 IRIS ODBC 驱动，并按所在环境准备 DSN 或连接参数。
2. 启动应用，在“连接设置”中配置连接并验证可用性。
3. 在“元素配置”中建立本项目需要的业务元素。
4. 在“SQL 规则”中添加参数化查询，设置输入元素和输出列映射。
5. 使用测试参数验证规则，确认无误后点击“保存”。
6. 返回“快捷查询”，输入任意可人工填写的元素并按 Enter 开始查询。

### SQL 规则示例

以下示例使用虚构的表名和字段名。先创建 `id_card_no` 与 `registration_no` 两个元素，再配置规则：

```sql
SELECT REGISTRATION_NO
FROM Demo.PatientRegistration
WHERE ID_CARD_NO = {{id_card_no}}
```

将结果列 `REGISTRATION_NO` 映射到元素 `registration_no`。运行时，`{{id_card_no}}` 会被编译为 ODBC 参数，不会直接进入 SQL 文本。

## 查询交互

- 人工字段按 Enter：取消旧运行，使用当前值开始新一轮查询。
- SQL 回填字段：单击聚焦，`Ctrl+C` 复制，双击或 F2 编辑，Enter 确认并重新查询，Esc 放弃编辑。
- 单条规则返回一行：自动选择；返回多行：等待使用者选择后继续级联。
- 查询条件可添加、移除和拖动排序；完成编辑后只保存元素固定 ID 和顺序，不保存患者值。
- 日期元素统一显示为 `yyyy-MM-dd`，数据库值包含时间时只保留日期部分。
- 单条 SQL 默认超时 15 秒、最多读取 500 行；一次查询最多执行 100 次规则。

## 本地数据与升级

用户数据固定保存在：

```text
%LocalAppData%\IrisQuickQuery
```

| 路径 | 内容 |
| --- | --- |
| `config.db` | 当前元素、SQL 规则、连接设置、加密密码、加密测试参数和界面设置 |
| `Logs` | 不含参数值和结果值的执行元数据 |
| `Backups` | 应用版本变化时生成的 SQLite 在线备份 |

程序文件与用户数据相互分离。覆盖安装、升级或替换绿色版程序目录不会删除现有配置；检测到版本变化时，应用会先备份数据库再执行无损迁移。

`.irisqconfig` 规则包不会包含服务器地址、用户名、密码、测试参数或患者数据。导入内容只进入当前编辑区，仍需确认并保存后才会生效。

## 开发与构建

### 开发环境

- .NET 8 SDK（仓库通过 `global.json` 指定 `8.0.301`，允许使用对应的最新补丁版本）；
- PowerShell；
- Inno Setup 6 或 7（仅生成安装包时需要）。

### 构建并运行测试

```powershell
.\build.ps1
```

脚本会依次还原依赖、运行 Release 自动化测试，并生成 self-contained x64 发布目录：

```text
artifacts\publish\win-x64
```

### 生成安装包

```powershell
.\build.ps1 -BuildInstaller
```

输出文件：

```text
artifacts\installer\IrisQuickQuery-Setup-1.0.15-win-x64.exe
```

构建脚本会查找仓库内工具目录或系统安装的 Inno Setup 6/7。

### 生成绿色版

```powershell
.\build.ps1 -BuildPortable
```

输出文件：

```text
artifacts\portable\IrisQuickQuery-Portable-1.0.15-win-x64.zip
```

绿色版不包含 `config.db`、`Logs`、`Backups`、连接配置、密码或患者数据。它与安装版共用当前 Windows 用户的本地数据目录。

> 正式发布时不得使用 `-SkipTests`，并须确认安装包文件名、绿色版文件名与应用内部版本号一致。

## 项目结构

```text
src/
├── IrisQuickQuery.App/             WPF 界面与应用启动
├── IrisQuickQuery.Core/            领域模型、SQL 校验与规则调度
└── IrisQuickQuery.Infrastructure/  SQLite、DPAPI、ODBC 与日志实现

tests/IrisQuickQuery.Core.Tests/    自动化测试
installer/                          Inno Setup 安装脚本
portable/                           绿色版使用说明
docs/                               产品要求、架构与设计规范
```

自动化测试覆盖 SQL 安全校验、类型转换、规则调度、循环收敛、冲突处理、多行选择、取消与撤销、SQLite 配置存储、旧数据迁移、规则包以及 WPF XAML 冒烟加载。

## 项目文档

- [长期产品要求与发布检查清单](docs/product-requirements.md)
- [架构说明](docs/architecture.md)
- [设计规范](docs/design-system.md)

修改产品行为、生成安装包或发布新版本前，请先阅读产品要求文档，并将其中的“必须”条目作为验收条件。
