namespace GptImageCli;

internal static class Usage
{
    public static void Write(TextWriter writer)
    {
        writer.WriteLine("gpt-image - GPT-Image-2 独立命令行图片生成工具");
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
        writer.WriteLine("  --mode <responses|images>    默认 responses；images 使用 /images/generations");
        writer.WriteLine("  --model <text-model>         Responses API 文本模型，默认 gpt-4.1");
        writer.WriteLine("  --image-model <model>        图片模型/部署名，默认 gpt-image-2");
        writer.WriteLine("  --api-version <version>      Azure/OpenAI 兼容网关需要时追加 api-version");
        writer.WriteLine("  --auth <auto|bearer|api-key> 默认 auto；Azure endpoint 自动使用 api-key 头");
        writer.WriteLine("  --size <WxH>                 默认 1024x1024");
        writer.WriteLine("  --quality <value>            默认 medium");
        writer.WriteLine("  --format <png|jpeg|webp>     默认 png");
        writer.WriteLine("  --n <count>                  生成数量，默认 1");
        writer.WriteLine("  --output <path>              输出目录或文件名，默认当前目录");
        writer.WriteLine("  --prompt-file <path>         从文件读取提示词");
        writer.WriteLine("  --timeout-minutes <minutes>  默认 10");
    }
}
