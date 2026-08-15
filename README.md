# GitHub Tool — Alife 插件

HttpClient 直调 GitHub REST API，零 MCP 桥接，WebUI 配置 Token 即可用。
本插件是 [KiraAI_github_tool_plugin](https://github.com/AinaLife-ai/KiraAI_github_tool_plugin) 的 Alife 版移植，功能一一对应，专为 Alife 4.2.x 生态与 Alife 系 Bot 设计。

## 功能

| 工具 | 说明 |
|------|------|
| `github_search` | 搜仓库/代码/Issues/用户 |
| `github_get` | 读文件 SHA、Issue 详情、PR 详情、PR 文件列表、状态、评论、Review |
| `github_read_file` | 读取文件实际内容（Base64 解码），返回内容 + SHA，支持 offset/limit 分页 |
| `github_list` | 列 commits / issues / pull requests |
| `github_create` | 创建仓库/文件/Issue/PR/分支/Review/Star/Release，删除文件/仓库(需开关)/分支/Release/评论，取消 Star |
| `github_update` | 更新 Issue（标题/正文/状态/标签/指派人/里程碑）、编辑/关闭 PR、更新 PR 分支 |
| `github_mutation` | 批量文件操作、发 Issue 评论、合并 PR |
| `github_fork` | Fork 仓库到个人或组织 |
| `github_check_token` | 检查 GitHub Token 是否已配置（可配置是否返回明文 Token） |

## 安装

1. 下载 [Alife.GitHubTool.zip](https://github.com/AinaLife-ai/Alife.GitHubTool/releases/latest)（或从插件市场安装）
2. 解压到 Alife 客户端的 `Plugins/Alife.GitHubTool/` 目录
3. 客户端「系统管理 → 插件环境 → 同步环境」，编译加载
4. 在角色配置中勾选模块「GitHub工具」（分类：AinaLife/GitHub），或编辑 `{角色目录}/index.json` 的 `Modules` 数组加入 `AinaLife.GitHubTool.GitHubToolModule`
5. 填写配置 `GitHub Token`（需 `repo` 权限），可选调整：
   - `文件内容最大返回字符数`（默认 10000）
   - `检查Token时返回明文`（默认关，仅调试）
   - `允许删除仓库`（默认关，不可逆，谨慎开启）
6. 重载角色配置并激活，AI 即可使用全部 GitHub 工具

## Token 配置

- GitHub → `Settings → Developer settings → Personal access tokens → Tokens (classic)`
- 勾选 `repo` 权限（完整 API 访问）
- 复制 token 填入插件配置
- 模块启动时会自动验证 token 并获取当前登录账号；AI 上下文会自动注入 token 状态提示

## 工作原理

- 所有请求走 `HttpClient` 直调 GitHub REST API（等价于 curl 直调），零 MCP 桥接、零第三方运行时依赖（仅 Newtonsoft.Json）
- 自动识别仓库默认分支（带缓存）：分支参数留空即自动使用 main/master
- 写文件无需先获取 SHA：`github_create(act=file)` / `github_mutation(act=files)` 自动判断新建还是更新，每个文件的真实成败单独反馈
- 输出精简为 AI token 友好的格式：搜索/列表只展示前 10~20 条，错误带 HTTP 状态码与 GitHub 详情
- 文件内容读取支持 Base64 解码、截断与分页提示（`还有更多: 是` + 建议 offset）

## 注意事项

- 删除仓库（`act=delete_repository`）默认被开关禁用，开启后不可逆，请谨慎
- 明文 Token 返回功能仅用于调试，开启后请注意日志与对话记录安全
- 数组参数（标签/指派/批量文件）传 JSON 数组字符串，如 `["bug","enhancement"]`、`[{"p":"a.txt","c":"content"}]`
- 所有工具函数由 AI 通过 XML 函数调用（`XmlFunctionCaller`），Implicit 文档模式省 token

## 许可证

AGPL-3.0
