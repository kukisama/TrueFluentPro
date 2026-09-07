using GptImageCli;

Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return await CliApplication.RunAsync(args, Console.Out, Console.Error);
