using GptImageCli;

Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return QueueApplication.Handles(args)
	? await QueueApplication.RunAsync(args, Console.Out, Console.Error)
	: await CliApplication.RunAsync(args, Console.Out, Console.Error);
