# 宋人书斋资产制作

## 输入与输出

- 输入：`design/宋人书斋资产源图_20261104/` 的 `book-spread.png`、`controls-atlas.png`、`ornaments-atlas.png`。
- 三张输入均为 AI 参考指定书斋设计生成的 2560×1440、high PNG；任务编号分别为 188、189、190。
- 输出：`Assets/Appearance/SongStudy/`，共 23 张 PNG，由现有 Avalonia `Assets/**` 规则嵌入，不在运行时调用生成服务。
- 源图仅用于制作，不将中文文字、控件值或业务图标烘焙进图像。

## 制作方式

Windows PowerShell 7 中执行项目根目录的 `tools/SongStudyAssets/Export-Assets.ps1`。默认拒绝覆盖已有输出；明确需要重制时使用 `-Overwrite`。也可通过 `-SourceDirectory`、`-OutputDirectory` 指定输入和输出。

脚本不清理任何目录、不删除旧设计、不读取配置密钥、不修改队列。依赖宿主自带 System.Drawing，C# 像素处理通过 LockBits 完成，不使用逐像素 PowerShell 慢循环。

## 切片契约

| 资产 | 来源与用途 |
| --- | --- |
| `book-r0-c0.png` 至 `book-r2-c4.png` | 书册 5×3 切片，固定书角／书脊，延展纸面与边缘 |
| `nav-idle.png`、`nav-selected.png` | 控件资产板上排，书签导航的普通／选中背景 |
| `seal-primary.png` | 控件资产板左下，开始／停止的朱印背景 |
| `panel-frame.png` | 控件资产板右下，备用细金纸面框；当前参数区仍用可伸缩真实描边 |
| `bamboo-left.png`、`landscape.png`、`bamboo-right.png`、`tassel.png` | 装饰资产板四格，均为非命中图层 |

书册横向切线为 0／48／1248／1312／2512／2560，纵向为 0／64／1376／1440。两张资产板分别按 2×2 固定分区裁切，去品红底后按可靠前景 alpha 边界加 6px 安全边修整；低透明度背景色差不再撑满整格。

## 运行时与维护

- `SongStudyBookFrame` 仅负责装饰，`PageHost` 和业务页面不是它的内容，不发生换壳迁移。
- 书斋样式随窗口预载，只有颜色资源字典在切换时局部合并／撤销。
- 可替换同名图片并重新构建；调整裁切规则后应复查实际组件尺寸，避免将整张资产板缩进单个按钮。
- 图片不承载点击逻辑，命令和可访问性名称仍由真实 Avalonia 控件提供。
- 生图状态、参数及尝试记录由既有生成队列保存；不旁写提示词、任务清单或额外 JSON 回执。