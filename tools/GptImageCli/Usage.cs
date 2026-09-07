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
        writer.WriteLine();
        writer.WriteLine("必要参数可用环境变量替代:");
        writer.WriteLine("  --endpoint    GPT_IMAGE_ENDPOINT / OPENAI_BASE_URL / AZURE_OPENAI_ENDPOINT");
        writer.WriteLine("  --api-key     GPT_IMAGE_API_KEY / OPENAI_API_KEY / AZURE_OPENAI_API_KEY");
        writer.WriteLine();
        writer.WriteLine("常用选项:");
        writer.WriteLine("  --mode <responses|images|edit> 默认 responses；images 文生图，edit 直接改图");
        writer.WriteLine("  --image <path>               edit 必填；PNG/JPEG/WebP，可重复传入多张参考图");
        writer.WriteLine("  --model <text-model>         Responses API 文本模型，默认 gpt-4.1");
        writer.WriteLine("  --image-model <model>        图片模型/部署名，默认 gpt-image-2");
        writer.WriteLine("  --api-version <version>      Azure/OpenAI 兼容网关需要时追加 api-version");
        writer.WriteLine("  --auth <auto|bearer|api-key> 默认 auto；Azure endpoint 自动使用 api-key 头");
        writer.WriteLine("  --size <WxH>                 默认 1024x1024");
        writer.WriteLine("  --quality <value>            默认 medium");
        writer.WriteLine("  --format <png|jpeg|webp>     默认 png");
        writer.WriteLine("  --n <count>                  生成数量，默认 1");
        writer.WriteLine("  --output <path>              输出目录或文件名，默认当前目录");
        writer.WriteLine("  --overwrite                  无值开关；显式允许覆盖，默认不覆盖已有文件");
        writer.WriteLine("  目录文件名含 GUID；多图文件名加 -01/-02；images/edit 按 n 预检，冲突退出 2。");
        writer.WriteLine("  Responses 数量未知，按实际返回数量命名；保存冲突退出 1，保留已保存文件报告。");
        writer.WriteLine("  --prompt-file <path>         从文件读取提示词");
        writer.WriteLine("  --timeout-minutes <minutes>  默认 10");
        writer.WriteLine("  --mask <path>                仅 edit；PNG 透明区域表示编辑区域，不保证像素锁定");
        writer.WriteLine("  --background <value>         auto/opaque/transparent；image2 透明背景为预览能力");
        writer.WriteLine("  --output-compression <0..100> 仅 jpeg/webp；整数");
        writer.WriteLine("  --moderation <auto|low>      内容审核级别");
        writer.WriteLine("  --user <string>              仅 images/edit；responses 明确拒绝");
        writer.WriteLine("  --json                      无值开关；stdout 单份 JSON 报告，进度在 stderr");
        writer.WriteLine("  api_error 仅含 root.error 的安全 code/type/param（最多 64 字符）及通用中文 message。");
        writer.WriteLine("  新参数仅显式提供才发送；quality: low/medium/high/auto；n: 1..10。");
        writer.WriteLine();
        writer.WriteLine("改图示例（endpoint/key 已设为环境变量）:");
        writer.WriteLine("  gpt-image --mode edit --image input.png --prompt \"把帆船改为绿色，保留背景\" --size 1024x640 --quality low --output edited.png");
        writer.WriteLine("  edit 使用 /images/edits，不需要文字模型或 Responses 会话；继续改图时将上次输出作为 --image。");
    }
}
