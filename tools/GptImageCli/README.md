# GPT-Image CLI

独立 C# 命令行图片生成工具，默认走 Responses API + `image_generation` tool，图片模型/部署名默认 `gpt-image-2`，不引入第三方依赖。

## Build

```bash
dotnet build tools/GptImageCli/GptImageCli.csproj
```

## Usage

```bash
dotnet run --project tools/GptImageCli -- \
  --endpoint https://example.openai.azure.com \
  --api-key <key> \
  --api-version preview \
  --model gpt-4.1 \
  --image-model gpt-image-2 \
  --prompt "A minimalist fluent app icon" \
  --output ./out
```

可用环境变量替代敏感参数：`GPT_IMAGE_ENDPOINT`、`GPT_IMAGE_API_KEY`，也兼容 `OPENAI_BASE_URL` / `OPENAI_API_KEY` 和 Azure OpenAI 环境变量。

默认认证为 `Bearer`；Azure OpenAI 域名会自动使用 `api-key` 头。网关需要指定认证方式时使用 `--auth bearer` 或 `--auth api-key`。
