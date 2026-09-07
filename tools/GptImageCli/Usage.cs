namespace GptImageCli;

internal static class Usage
{
    public static void Write(TextWriter writer)
    {
        writer.WriteLine("gpt-image - GPT-Image-2 独立命令行生图/改图工具");
        writer.WriteLine();
        writer.WriteLine("用法:");
        writer.WriteLine("  gpt-image --endpoint <url> --api-key <key> --prompt <text> [options]");
        writer.WriteLine("  cat prompt.txt | gpt-image --endpoint <url> --api-key <key>");
        writer.WriteLine("  gpt-image --endpoint-name <友好名称> --prompt <text>");
        writer.WriteLine("  --endpoint-name <Name> / --endpoint-id <Id> 互斥；名称精确匹配（忽略大小写），ID 精确匹配。");
        writer.WriteLine("  --list-endpoints 离线列出启用图片节点的 id/name/models；无需 prompt/key，--json 为单份报告。");
        writer.WriteLine("  未列出的节点及具体原因显示在 stderr；模型须勾选图片生成能力，仅有 gpt-image-2 名称不够。");
        writer.WriteLine("  --config <path> 只读指定配置；默认 %APPDATA%/TrueFluentPro/config.json，最多 4 MiB，不创建或修改。");
        writer.WriteLine("  --no-config 禁用回退；不能与 --config、节点选择器、--list-endpoints 同用。");
        writer.WriteLine("  指定名称/ID：节点配置的地址和 Key 覆盖参数及环境，仅本次请求生效，不修改全局数据。");
        writer.WriteLine("  无选择器：参数 > 环境 > 配置回退；连接齐全时不读配置（即使指定 --config）。--help 不读配置。");
        writer.WriteLine("  支持环境注入、节点名称/ID、明文 --endpoint/--api-key 三种方式；明文密钥可能进入历史与进程列表，不建议。");
        writer.WriteLine("  --endpoint 始终是 URL；借用配置 key 必须与 BaseUrl 规范 URL 完全一致，不按 host/完整 API 路径猜测。");
        writer.WriteLine("  自动选有效 ImageModelRef，否则仅选唯一图片节点；失效引用、多节点、重名均需明确选择。");
        writer.WriteLine("  模型优先 --image-model / GPT_IMAGE_MODEL，其次所选节点有效默认引用、唯一 gpt-image-2、唯一图片模型。");
        writer.WriteLine("  配置部署名用于请求，逻辑模型用于尺寸校验；不导入主程序的 size/quality/n 等生成参数。");
        writer.WriteLine("  配置 AAD/自定义 profile 不自动支持；请用 --no-config 显式 URL/key/--auth 独立运行。");
        writer.WriteLine("  配置 Auto 认证：APIM/OpenAI Bearer、Azure api-key；--auth bearer/api-key 覆盖，auto 沿用节点规则。不重试或跟随重定向。");
        writer.WriteLine();
        writer.WriteLine("必要参数可用环境变量替代:");
        writer.WriteLine("  --endpoint    GPT_IMAGE_ENDPOINT / OPENAI_BASE_URL / AZURE_OPENAI_ENDPOINT");
        writer.WriteLine("  --api-key     GPT_IMAGE_API_KEY / OPENAI_API_KEY / AZURE_OPENAI_API_KEY");
        writer.WriteLine();
        writer.WriteLine("常用选项:");
        writer.WriteLine("  --mode <responses|images|edit> 默认 images 文生图；edit 直接改图");
        writer.WriteLine("  --image <path>               edit 必填；PNG/JPEG/WebP，可重复传入多张参考图");
        writer.WriteLine("  --model <text-model>         Responses API 文本模型，默认 gpt-4.1");
        writer.WriteLine("  --image-model <model>        图片模型/部署名；独立连接默认 gpt-image-2，配置连接按上述顺序选择");
        writer.WriteLine("  --api-version <version>      Azure/OpenAI 兼容网关需要时追加 api-version");
        writer.WriteLine("  --auth <auto|bearer|api-key> 默认 auto；Azure endpoint 自动使用 api-key 头");
        writer.WriteLine("  --size <WxH>                 默认 1024x640");
        writer.WriteLine("  --quality <value>            默认 medium");
        writer.WriteLine("  --format <png|jpeg|webp>     默认 png");
        writer.WriteLine("  --n <count>                  生成数量，默认 1");
        writer.WriteLine("  --output <path>              默认 .（当前工作目录）；省略或只传目录均自动命名");
        writer.WriteLine("  --overwrite                  无值开关；显式允许覆盖，默认不覆盖已有文件");
        writer.WriteLine("  目录自动创建，文件名含时间戳和 GUID；无需调用方生成 ID 或预先检查路径。");
        writer.WriteLine("  已有目录、末尾为目录分隔符或无扩展名的路径按目录处理；新建带点目录请加末尾分隔符。");
        writer.WriteLine("  其他路径按文件处理：单图保留指定文件名，多图加 -01/-02；images/edit 内部预检冲突退出 2。");
        writer.WriteLine("  Responses 数量未知，按实际返回数量命名；保存冲突退出 1，保留已保存文件报告。");
        writer.WriteLine("  显式使用 --mode responses 时，请自行通过 --size 适配服务端支持的尺寸。");
        writer.WriteLine("  --prompt-file <path>         从文件读取提示词");
        writer.WriteLine("  --timeout-minutes <minutes>  默认 10");
        writer.WriteLine("  --mask <path>                仅 edit；PNG 透明区域表示编辑区域，不保证像素锁定");
        writer.WriteLine("  --background <value>         auto/opaque/transparent；image2 透明背景为预览能力");
        writer.WriteLine("  --output-compression <0..100> 仅 jpeg/webp；整数");
        writer.WriteLine("  --moderation <auto|low>      内容审核级别");
        writer.WriteLine("  --user <string>              仅 images/edit；responses 明确拒绝");
        writer.WriteLine("  --json                      无值开关；stdout 单份 JSON 报告，进度在 stderr");
        writer.WriteLine("  api_error 仅含 root.error 的安全 code/type/param（最多 64 字符）及通用中文 message。");
        writer.WriteLine("  mask/background/output-compression/moderation/user 默认不设置、不发送；quality: low/medium/high/auto；n: 1..10。");
        writer.WriteLine("  默认无参考图；独立连接默认不追加 api-version（可由环境变量提供）；配置连接按 profile 模板使用版本；--json 默认关闭。");
        writer.WriteLine();
        writer.WriteLine("生图示例（endpoint/key 已设为环境变量）:");
        writer.WriteLine("  gpt-image --prompt \"一艘绿色帆船\"");
        writer.WriteLine("  gpt-image --prompt \"一艘绿色帆船\" --json");
        writer.WriteLine();
        writer.WriteLine("改图示例（endpoint/key 已设为环境变量）:");
        writer.WriteLine("  gpt-image --mode edit --image input.png --prompt \"把帆船改为绿色，保留背景\"");
        writer.WriteLine("  edit 使用 /images/edits，不需要文字模型或 Responses 会话；继续改图时将上次输出作为 --image。");
    }
}
