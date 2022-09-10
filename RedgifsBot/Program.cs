using Serilog;
using Serilog.Events;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;

// ReSharper disable MethodSupportsCancellation

namespace RedgifsBot
{
	internal static class Program
	{
		const string _token = "here goes the token";
		private static readonly TelegramBotClient BotClient;
		private static readonly ILogger Logger;

		static Program()
		{
			BotClient = new TelegramBotClient(_token);
			Logger = new LoggerConfiguration()
				.WriteTo.Console(LogEventLevel.Information)
#if !DEBUG
				.WriteTo.File("log")
#endif
				.CreateLogger();
		}

		private static async Task Main()
		{
			var receiverOptions = new ReceiverOptions
			{
				AllowedUpdates = { } // receive all update types
			};

			BotClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions);

			var me = await BotClient.GetMeAsync();
			Logger.Information("Start listening for {0}", me.Username);

			while (true)
			{
				var cki = Console.ReadKey();
				if (cki.Key == ConsoleKey.Escape)
				{
					return;
				}
			}
		}

		private static async void HandleUpdateAsync(ITelegramBotClient c, Update update, CancellationToken cts)
		{
			var message = update?.Message;
			if (message is null)
				return;

			var chat = message.Chat;
			var senderId = chat.Id;
			if (senderId < 0)
				return;

			var text = message.Text;
			if (text is not null)
			{
				Logger.Information("Received '{text}' from {id}:{name} @{username}", text, senderId, chat.FirstName + ' ' + chat.LastName, chat.Username);
				await HandleText(message, senderId, text);
				return;
			}

			await BotClient.SendTextMessageAsync(senderId, "ERROR: Message is not Redgifs link'");
		}

		private static async Task HandleText(Message message, long senderId, string text)
		{
			var urlEntity = message?.Entities?.FirstOrDefault(e => e.Type == MessageEntityType.Url);
			if (urlEntity is null)
			{
				await BotClient.SendTextMessageAsync(senderId, "ERROR: URL not found");
				return;
			}

			var link = text.Substring(urlEntity.Offset, urlEntity.Length);
			if (!link.Contains("redgifs.com/watch/"))
			{
				await BotClient.SendTextMessageAsync(senderId, "ERROR: Link does not contain 'redgifs.com/watch/'");
				return;
			}

			string videoUrl = null;
			int? videoWidth = null;
			int? videoHeight = null;
			int? videoDuration = null;

			using (var client = new HttpClient())
			{
				using var response = await client.GetAsync(link);
				await using var streamToReadFrom = await response.Content.ReadAsStreamAsync();
				using var sr = new StreamReader(streamToReadFrom);
				while (!sr.EndOfStream)
				{
					var line = await sr.ReadLineAsync();
					const string urlStartMarker = "meta property=\"og:video\" content=\"";
					const string urlEndMarker = ".mp4";

					const string widthStartMarker = "meta property=\"og:video:width\" content=\"";
					const string widthEndMarker = "\"><meta property=\"og:video:height\"";

					const string heightStartMarker = "meta property=\"og:video:height\" content=\"";
					const string heightEndMarker = "\"><meta property=\"og:video:iframe\"";

					const string durationStartMarker = "meta property=\"og:video:duration\" content=\"";
					const string durationEndMarker = "\"><meta property=\"og:video\"";

					if (line is not null)
					{
						if (line.Contains(urlStartMarker))
						{
							var start = line.IndexOf(urlStartMarker);
							var end = line.IndexOf(urlEndMarker);
							videoUrl = line[(start + urlStartMarker.Length)..(end + urlEndMarker.Length)];
							if (videoUrl.Contains("-mobile"))
								videoUrl = videoUrl.Replace("-mobile", string.Empty);

							Logger.Information("Found video url {url}", videoUrl);
						}

						if (line.Contains(widthStartMarker))
						{
							var start = line.IndexOf(widthStartMarker);
							var end = line.IndexOf(widthEndMarker);
							string widthString = line[(start + widthStartMarker.Length)..end];
							var didParse = int.TryParse(widthString, out int result);
							if (didParse)
							{
								videoWidth = result;
								Logger.Information("Found width {width}", videoWidth);
							}
							else
							{
								Logger.Error("Couldn't get width from '{widthStr}'", widthString);
							}
						}

						if (line.Contains(heightStartMarker))
						{
							var start = line.IndexOf(heightStartMarker);
							var end = line.IndexOf(heightEndMarker);
							string heightString = line[(start + heightStartMarker.Length)..end];
							var didParse = int.TryParse(heightString, out int result);
							if (didParse)
							{
								videoHeight = result;
								Logger.Information("Found height {height}", videoHeight);
							}
							else
							{
								Logger.Error("Couldn't get height from '{heightStr}'", heightString);
							}
						}

						if (line.Contains(durationStartMarker))
						{
							var start = line.IndexOf(durationStartMarker);
							var end = line.IndexOf(durationEndMarker);
							string durationString = line[(start + durationStartMarker.Length)..end];
							var didParse = int.TryParse(durationString, out int result);
							if (didParse)
							{
								videoDuration = result;
								Logger.Information("Found duration {duration}", videoDuration);
							}
							else
							{
								Logger.Error("Couldn't get duration from '{durationStr}'", durationString);
							}
						}

						break;
					}
				}
			}

			if (videoUrl is null)
			{
				await BotClient.SendTextMessageAsync(senderId, "ERROR: Was not able to find video on the page");
				return;
			}

			using var timer = new System.Timers.Timer(TimeSpan.FromSeconds(5).TotalMilliseconds)
			{
				AutoReset = true,
				Enabled = false
			};

			timer.Elapsed += async (_, _) => await BotClient.SendChatActionAsync(senderId, ChatAction.UploadVideo);

			await BotClient.SendChatActionAsync(senderId, ChatAction.UploadVideo);
			timer.Start();

			using (var client = new HttpClient())
			{
				Logger.Information("Started sending the video to {id}:{name} @{username}", senderId, message.From.FirstName + ' ' + message.From.LastName, message.From.Username);

				using var response = await client.GetAsync(videoUrl);
				await using var fs = await response.Content.ReadAsStreamAsync();
				var iof = new InputOnlineFile(fs, Path.GetRandomFileName());
				await BotClient.SendVideoAsync(senderId, iof, videoDuration, videoWidth, videoHeight);

				Logger.Information("Finished sending the video to {id}:{name} @{username}", senderId, message.From.FirstName + ' ' + message.From.LastName, message.From.Username);
			}

			timer.Stop();
		}

		private static void HandleErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken cts)
		{
			var ErrorMessage = exception switch
			{
				ApiRequestException apiRequestException
					=> $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
				_ => exception.Message
			};

			Logger.Error(ErrorMessage);
		}
	}
}