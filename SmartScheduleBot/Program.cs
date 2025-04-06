using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

class Program
{
    private static TelegramBotClient botClient;
    private static TelegramBotClient targetBotClient;
    private static string targetBotToken = "7373851133:AAEpEm7hSnv1HEYoSJEZjXksBOUe6_guaZk";
    private static long targetChatId = 6010438305;
    private static string baseUrl = "https://smartschedule-k0ex.onrender.com";
    private static string selectedFaculty = "";
    private static string selectedCourse = "";
    private static string selectedGroup = "";
    private static string selectedTeacherCode = null;
    private static bool isTeacherMode = false;
    private static readonly Dictionary<long, bool> awaitingAdminCode = new Dictionary<long, bool>();
    private static readonly HashSet<long> uniqueUsers = new HashSet<long>();
    private static readonly Dictionary<long, DateTime> activeUsers = new Dictionary<long, DateTime>();
    private static int statsMessageId = 0;

    public static async Task Main(string[] args)
    {
        // Bot tokenini environment variable’dan olish
        var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "7355644988:AAEyex_eompvTQ6ju_gx0cR9dmOonraBRRE";
        botClient = new TelegramBotClient(botToken);
        targetBotClient = new TelegramBotClient(targetBotToken);

        var me = await botClient.GetMeAsync();
        Console.WriteLine($"✅ Бот {me.FirstName} ба кор андохта шуд.");

        // Webhook sozlash
        var webhookUrl = $"{Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") ?? "https://your-app-name.onrender.com"}/webhook";
        await botClient.SetWebhookAsync(webhookUrl);
        Console.WriteLine($"Webhook set to: {webhookUrl}");

        // Boshlang‘ich statistika xabarini yuborish
        await SendInitialStatsMessage();

        // HTTP serverni ishga tushirish
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        // Webhook endpoint
        app.MapPost("/webhook", async (HttpContext context) =>
        {
            try
            {
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var update = JsonSerializer.Deserialize<Update>(body);
                await HandleUpdateAsync(botClient, update, CancellationToken.None);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Webhook xatoligi: {ex.Message}");
                return Results.StatusCode(500);
            }
        });

        // Root sahifa (Render uchun)
        app.MapGet("/", () => "Bot is running on Render!");

        // Statistika yangilash (har 30 sekundda)
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await UpdateStatsMessage();
                await Task.Delay(30000); // 30 sekund
            }
        });

        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        app.Run($"http://0.0.0.0:{port}");
    }

    private static async Task SendInitialStatsMessage()
    {
        try
        {
            int totalUsers = uniqueUsers.Count;
            int activeUsersCount = activeUsers.Count(u => (DateTime.Now - u.Value).TotalMinutes <= 5);
            string statsMessage = $"📊 Статистикаи корбарон:\n" +
                                 $"Шумораи умумии корбарон: {totalUsers}\n" +
                                 $"Корабарони фаол дар айни замон: {activeUsersCount}";

            var sentMessage = await targetBotClient.SendTextMessageAsync(targetChatId, statsMessage);
            statsMessageId = sentMessage.MessageId;
            Console.WriteLine("✅ Boshlang‘ich statistika xabari yuborildi.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогӣ (SendInitialStatsMessage): {ex.Message}");
        }
    }

    private static async Task UpdateStatsMessage()
    {
        try
        {
            int totalUsers = uniqueUsers.Count;
            int activeUsersCount = activeUsers.Count(u => (DateTime.Now - u.Value).TotalMinutes <= 5);
            string statsMessage = $"📊 Статистикаи корбарон:\n" +
                                 $"Шумораи умумии корбарон: {totalUsers}\n" +
                                 $"Корабарони фаол дар айни замон: {activeUsersCount}";

            await targetBotClient.EditMessageTextAsync(targetChatId, statsMessageId, statsMessage);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("message is not modified")) return; // Agar xabar o‘zgarmasa, e’tibor bermaslik
            Console.WriteLine($"❌ Хатогӣ (UpdateStatsMessage): {ex.Message}");
        }
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Type == UpdateType.Message && update.Message?.Text != null)
        {
            long chatId = update.Message.Chat.Id;
            long userId = update.Message.From.Id;
            string messageText = update.Message.Text;

            uniqueUsers.Add(userId);
            activeUsers[userId] = DateTime.Now;

            if (awaitingAdminCode.ContainsKey(chatId) && awaitingAdminCode[chatId])
            {
                if (messageText == "13082003")
                {
                    awaitingAdminCode[chatId] = false;
                    Console.WriteLine($"📩 Корбар {userId} коди дурусти админро ворид кард.");
                    await ExportTeachersList(chatId);
                }
                else
                {
                    awaitingAdminCode[chatId] = false;
                    await botClient.SendTextMessageAsync(chatId, "❌ Код нодуруст аст! Лутфан дубора бо фармони /admin ворид кунед.");
                }
                return;
            }

            switch (messageText)
            {
                case "/start":
                    Console.WriteLine($"📩 Корбар {userId} фармони /start-ро фиристод.");
                    awaitingAdminCode[chatId] = false;
                    await botClient.SetChatMenuButtonAsync(chatId, new MenuButtonDefault());
                    bool isSubscribed = await IsUserSubscribed(userId);
                    if (isSubscribed)
                    {
                        isTeacherMode = false;
                        selectedTeacherCode = null;
                        await SendWelcomeMessage(chatId);
                        await SendUserTypeSelection(chatId);
                    }
                    else
                    {
                        await SendSubscriptionPrompt(chatId);
                    }
                    break;

                case "/help":
                    Console.WriteLine($"📩 Корбар {userId} фармони /help-ро фиристод.");
                    await SendHelpMessage(chatId);
                    break;

                case "/admin":
                    awaitingAdminCode[chatId] = true;
                    await botClient.SendTextMessageAsync(chatId, "🔑 Лутфан коди админро ворид кунед:");
                    Console.WriteLine($"📩 Корбар {userId} фармони /admin-ро фиристод.");
                    break;

                default:
                    if (isTeacherMode && selectedTeacherCode == null && !messageText.StartsWith("/"))
                    {
                        await HandleTeacherCodeInput(chatId, messageText);
                    }
                    else if (isTeacherMode && selectedTeacherCode != null && !messageText.StartsWith("/"))
                    {
                        await HandleDaySelection(chatId, messageText);
                    }
                    else if (!isTeacherMode && !messageText.StartsWith("/"))
                    {
                        await HandleDaySelection(chatId, messageText);
                    }
                    else if (messageText.StartsWith("/"))
                    {
                        await botClient.SendTextMessageAsync(chatId,
                            "❌ Фармони номаълум! Барои дидани фармонҳои мавҷуд /help-ро пахш кунед.");
                        Console.WriteLine($"📩 Корбар {userId} фармони нодуруст фиристод: {messageText}");
                    }
                    break;
            }
        }
        else if (update.Type == UpdateType.CallbackQuery)
        {
            long userId = update.CallbackQuery.From.Id;
            uniqueUsers.Add(userId);
            activeUsers[userId] = DateTime.Now;
            await HandleCallbackQuery(update.CallbackQuery);
        }
    }

    private static async Task<bool> IsUserSubscribed(long userId)
    {
        try
        {
            var chatMember = await botClient.GetChatMemberAsync("@Career1ink", userId);
            return chatMember.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Санҷиши обуна бо хатогӣ рӯ ба рӯ шуд: {ex.Message}");
            return false;
        }
    }

    private static async Task SendWelcomeMessage(long chatId)
    {
        string welcomeMessage =
            "🎉 Хуш омадед ба *SmartScheduleBot*! Ман ба шумо дар ёфтани осон ва зуд жадвали дарсӣ кӯмак мерасонам.\n\n" +
            "📚 *Имкониятҳои ман:*\n" +
            "- Барои донишҷӯён жадвали дарсӣ аз рӯи факулта, курс ва гурӯҳ.\n" +
            "- Барои муаллимон жадвали дарсӣ тавассути коди махсус.\n" +
            "- Жадвали дақиқ барои ҳар рӯз ва дидани жадвали пурра тавассути тугмаи веб.\n\n" +
            "🔧 *Фармонҳои мавҷуд:*\n" +
            "/start - Оғози дубораи бот\n" +
            "/help - Кӯмак ва иттилоот\n" +
            "/admin - Боргирии рӯйхати муаллимон барои админҳо\n\n" +
            "📩 *Барои пешниҳод ва талабҳо:* Ба @future1308 нависед!";

        await botClient.SendTextMessageAsync(chatId, welcomeMessage, parseMode: ParseMode.Markdown);
        Console.WriteLine($"📩 Ба корбар {chatId} паёми хуш омадед фиристода шуд.");
    }

    private static async Task SendHelpMessage(long chatId)
    {
        string helpMessage =
            "ℹ️ *Ёрдамчии SmartScheduleBot*\n\n" +
            "Ман ба шумо дар ёфтани жадвали дарсӣ осон кӯмак мерасонам:\n" +
            "1. Агар донишҷӯ бошед: Факулта, курс ва гурӯҳатонро интихоб кунед.\n" +
            "2. Агар муаллим бошед: Коди махсусатонро ворид кунед.\n" +
            "3. Барои жадвали рӯзона рӯзро интихоб кунед ё тавассути тугмаи *Пурра* жадвали комилро бинед.\n\n" +
            "🔧 *Фармонҳо:*\n" +
            "/start - Оғоз\n" +
            "/help - Ин кӯмак\n" +
            "/admin - Рӯйхати муаллимон (фақат барои админҳо)\n\n" +
            "📩 Агар савол ё пешниҳод дошта бошед, ба @future1308 муроҷиат кунед!";

        await botClient.SendTextMessageAsync(chatId, helpMessage, parseMode: ParseMode.Markdown);
        Console.WriteLine($"📩 Ба корбар {chatId} паёми кӯмак фиристода шуд.");
    }

    private static async Task SendUserTypeSelection(long chatId)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("👨‍🎓 Донишҷӯ", "student") },
            new[] { InlineKeyboardButton.WithCallbackData("👨‍🏫 Муаллим", "teacher") }
        });
        await botClient.SendTextMessageAsync(chatId, "📌 Ба кадом сифат идома додан мехоҳед?", replyMarkup: inlineKeyboard);
        Console.WriteLine($"📩 Ба корбар {chatId} савол оиди интихоби сифат фиристода шуд.");
    }

    private static async Task SendSubscriptionPrompt(long chatId)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("📢 Ба канал обуна шавед", "https://t.me/Career1ink") },
            new[] { InlineKeyboardButton.WithCallbackData("✅ Обунаро санҷед", "check_subscription") }
        });
        await botClient.SendTextMessageAsync(chatId, "❌ Лутфан аввал ба канал обуна шавед, баъд аз бот истифода баред:", replyMarkup: inlineKeyboard);
        Console.WriteLine($"📢 Ба корбар {chatId} саволи обунашавӣ фиристода шуд.");
    }

    private static async Task SendFaculties(long chatId)
    {
        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync($"{baseUrl}/get_faculties");
            var faculties = JsonSerializer.Deserialize<string[]>(response);

            if (faculties == null || faculties.Length == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ Факултаҳо ёфт нашуданд!");
                return;
            }

            var buttons = faculties.Select(f => InlineKeyboardButton.WithCallbackData(f, $"faculty_{f}")).Chunk(2).ToArray();
            var keyboard = new InlineKeyboardMarkup(buttons);
            await botClient.SendTextMessageAsync(chatId, "🏫 Факултаро интихоб кунед:", replyMarkup: keyboard);
            Console.WriteLine($"📋 Ба корбар {chatId} рӯйхати факултаҳо фиристода шуд.");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогӣ: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани факултаҳо хатогӣ: {ex.Message}");
        }
    }

    private static async Task SendCourses(long chatId, string faculty)
    {
        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync($"{baseUrl}/get_courses?faculty={Uri.EscapeDataString(faculty)}");
            var courses = JsonSerializer.Deserialize<string[]>(response);

            if (courses == null || courses.Length == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ Курсҳо ёфт нашуданд!");
                return;
            }

            var buttons = courses.Select(c => InlineKeyboardButton.WithCallbackData(c, $"course_{faculty}_{c}")).Chunk(2).ToArray();
            var backButton = new[] { InlineKeyboardButton.WithCallbackData("⬅️ Бозгашт", "back_faculty") };
            var keyboard = new InlineKeyboardMarkup(buttons.Append(backButton).ToArray());
            await botClient.SendTextMessageAsync(chatId, $"📚 {faculty} - Курсро интихоб кунед:", replyMarkup: keyboard);
            Console.WriteLine($"📋 Курсҳои факултаи {faculty} ба корбар {chatId} фиристода шуд.");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогӣ: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани курсҳо хатогӣ: {ex.Message}");
        }
    }

    private static async Task SendGroups(long chatId, string faculty, string course)
    {
        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync($"{baseUrl}/get_groups?faculty={Uri.EscapeDataString(faculty)}&course={Uri.EscapeDataString(course)}");
            var groups = JsonSerializer.Deserialize<string[]>(response);

            if (groups == null || groups.Length == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ Гурӯҳҳо ёфт нашуданд!");
                return;
            }

            var buttons = groups.Select(g => InlineKeyboardButton.WithCallbackData(g, $"group_{faculty}_{course}_{g}")).Chunk(2).ToArray();
            var backButton = new[] { InlineKeyboardButton.WithCallbackData("⬅️ Бозгашт", $"back_course_{faculty}") };
            var keyboard = new InlineKeyboardMarkup(buttons.Append(backButton).ToArray());
            await botClient.SendTextMessageAsync(chatId, $"📄 {faculty} - {course} - Гурӯҳро интихоб кунед:", replyMarkup: keyboard);
            Console.WriteLine($"📋 Гурӯҳҳои {faculty}/{course} ба корбар {chatId} фиристода шуд.");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогӣ: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани гурӯҳҳо хатогӣ: {ex.Message}");
        }
    }

    public static async Task UpdateMenuButton(long chatId, string faculty, string course, string group)
    {
        string webAppUrl = $"{baseUrl}/{faculty}/{course}/{group}";
        try
        {
            var newMenuButton = new MenuButtonWebApp
            {
                Text = "📚 Пурра",
                WebApp = new WebAppInfo { Url = webAppUrl }
            };
            await botClient.SetChatMenuButtonAsync(chatId: chatId, menuButton: newMenuButton);
            Console.WriteLine($"🔄 Тугмаи Web App барои {chatId} нав карда шуд: {webAppUrl}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Дар нав кардани тугмаи Web App хатогӣ: {ex.Message}");
        }
    }

    public static async Task UpdateTeacherMenuButton(long chatId, string teacherCode)
    {
        string webAppUrl = $"{baseUrl}/teacher/{teacherCode}";
        try
        {
            var newMenuButton = new MenuButtonWebApp
            {
                Text = "📚 Пурра",
                WebApp = new WebAppInfo { Url = webAppUrl }
            };
            await botClient.SetChatMenuButtonAsync(chatId: chatId, menuButton: newMenuButton);
            await botClient.SendTextMessageAsync(chatId, "📅 Барои дидани жадвали пурра тугмаи *Пурра*-ро пахш кунед!");
            Console.WriteLine($"🔄 Тугмаи Web App барои {chatId} нав карда шуд: {webAppUrl}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Дар нав кардани тугмаи Web App хатогӣ: {ex.Message}");
        }
    }

    private static async Task AskForDaySelection(long chatId, bool isTeacher = false)
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "📅 Имрӯз", "📅 Фардо" },
            new KeyboardButton[] { "Душанбе", "Сешанбе", "Чоршанбе" },
            new KeyboardButton[] { "Панҷшанбе", "Ҷумъа", "Шанбе" }
        })
        {
            ResizeKeyboard = true
        };
        string message = "📌 Лутфан рӯзи дилхоҳро интихоб кунед:";
        await botClient.SendTextMessageAsync(chatId, message, replyMarkup: keyboard);
        Console.WriteLine($"📅 Ба корбар {chatId} саволи интихоби рӯз фиристода шуд (муаллим: {isTeacher}).");
    }

    private static async Task HandleDaySelection(long chatId, string selectedDay)
    {
        string dayToFetch = selectedDay switch
        {
            "📅 Имрӯз" => DateTime.Today.ToString("dddd", new CultureInfo("tg-TJ")),
            "📅 Фардо" => DateTime.Today.AddDays(1).ToString("dddd", new CultureInfo("tg-TJ")),
            "Душанбе" => "Душанбе",
            "Сешанбе" => "Сешанбе",
            "Чоршанбе" => "Чоршанбе",
            "Панҷшанбе" => "Панҷшанбе",
            "Ҷумъа" => "Ҷумъа",
            "Шанбе" => "Шанбе",
            _ => selectedDay
        };

        string webAppUrl;
        if (isTeacherMode && selectedTeacherCode != null)
        {
            webAppUrl = $"{baseUrl}/get_teacher_day/{selectedTeacherCode}?day={Uri.EscapeDataString(dayToFetch)}";
            Console.WriteLine($"📤 Супориши жадвали рӯзона барои муаллим: {webAppUrl}");
        }
        else
        {
            if (string.IsNullOrEmpty(selectedFaculty) || string.IsNullOrEmpty(selectedCourse) || string.IsNullOrEmpty(selectedGroup))
            {
                await botClient.SendTextMessageAsync(chatId, "❌ Лутфан аввал факулта, курс ва гурӯҳро интихоб кунед!");
                return;
            }
            webAppUrl = $"{baseUrl}/get_day/{selectedFaculty}/{selectedCourse}/{selectedGroup}?day={Uri.EscapeDataString(dayToFetch)}";
            Console.WriteLine($"📤 Супориши жадвали рӯзона барои донишҷӯ: {webAppUrl}");
        }

        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync(webAppUrl);
            const int maxLength = 4000;
            if (response.Length <= maxLength)
            {
                await botClient.SendTextMessageAsync(chatId, response, parseMode: ParseMode.Html);
                Console.WriteLine($"✅ Ба корбар {chatId} жадвали рӯзона фиристода шуд.");
            }
            else
            {
                int partsCount = (response.Length + maxLength - 1) / maxLength;
                for (int i = 0; i < partsCount; i++)
                {
                    int start = i * maxLength;
                    int length = Math.Min(maxLength, response.Length - start);
                    string part = response.Substring(start, length);
                    await botClient.SendTextMessageAsync(chatId, part, parseMode: ParseMode.Html);
                    Console.WriteLine($"✅ Ба корбар {chatId} қисми жадвал {i + 1}/{partsCount} фиристода шуд.");
                    await Task.Delay(500);
                }
            }
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогӣ: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани жадвал хатогӣ: {ex.Message}");
        }
    }

    private static async Task HandleTeacherCodeInput(long chatId, string code)
    {
        try
        {
            using var client = new HttpClient();
            var content = new StringContent(JsonSerializer.Serialize(new { teacher_code = code }), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{baseUrl}/check_teacher_code", content);
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(await response.Content.ReadAsStringAsync());

            if (result["status"] == "success")
            {
                selectedTeacherCode = code;
                isTeacherMode = true;
                Console.WriteLine($"✅ Коди муаллим насб шуд: {selectedTeacherCode}, isTeacherMode: {isTeacherMode}");
                await botClient.SendTextMessageAsync(chatId, $"🎉 Хуш омадед, Устод {result["teacher_name"]}!");
                await UpdateTeacherMenuButton(chatId, code);
                await AskForDaySelection(chatId, true);
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "❌ Код нодуруст аст! Лутфан дубора код ворид кунед.");
            }
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогӣ: {ex.Message}");
            Console.WriteLine($"❌ Дар санҷиши код хатогӣ: {ex.Message}");
        }
    }

    private static async Task ExportTeachersList(long chatId)
    {
        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync($"{baseUrl}/get_teachers_with_codes");
            var teachers = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(response);

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Рӯйхати Муаллимон");
                worksheet.Cell(1, 1).Value = "Муаллим";
                worksheet.Cell(1, 2).Value = "Коди махсус";

                for (int i = 0; i < teachers.Count; i++)
                {
                    worksheet.Cell(i + 2, 1).Value = teachers[i]["teacher_name"];
                    worksheet.Cell(i + 2, 2).Value = teachers[i]["teacher_code"];
                }

                string fileName = "Рӯйхати_Муаллимон.xlsx";
                workbook.SaveAs(fileName);

                using (var stream = new FileStream(fileName, FileMode.Open))
                {
                    await botClient.SendDocumentAsync(chatId, new InputFileStream(stream, fileName), caption: "📋 Рӯйхати муаллимон бо кодҳои махсус!");
                }
                File.Delete(fileName);
            }
            Console.WriteLine($"✅ Ба корбар {chatId} рӯйхати муаллимон фиристода шуд.");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогӣ: {ex.Message}");
            Console.WriteLine($"❌ Дар содироти рӯйхати муаллимон хатогӣ: {ex.Message}");
        }
    }

    private static async Task HandleCallbackQuery(CallbackQuery callbackQuery)
    {
        long chatId = callbackQuery.Message.Chat.Id;
        int messageId = callbackQuery.Message.MessageId;
        string data = callbackQuery.Data;
        Console.WriteLine($"📩 Callback қабул шуд: {data}");

        await botClient.DeleteMessageAsync(chatId, messageId);

        if (data == "check_subscription")
        {
            bool isSubscribed = await IsUserSubscribed(callbackQuery.From.Id);
            if (isSubscribed)
            {
                await SendWelcomeMessage(chatId);
                await SendUserTypeSelection(chatId);
            }
            else
            {
                await SendSubscriptionPrompt(chatId);
            }
        }
        else if (data == "student")
        {
            isTeacherMode = false;
            selectedTeacherCode = null;
            await SendFaculties(chatId);
        }
        else if (data == "teacher")
        {
            isTeacherMode = true;
            selectedFaculty = selectedCourse = selectedGroup = "";
            selectedTeacherCode = null;
            await botClient.SendTextMessageAsync(chatId, "🔑 Лутфан коди махсуси худро ворид кунед:");
        }
        else if (data.StartsWith("faculty_"))
        {
            selectedFaculty = data.Substring(8);
            await SendCourses(chatId, selectedFaculty);
        }
        else if (data.StartsWith("course_"))
        {
            string[] parts = data.Substring(7).Split('_');
            selectedFaculty = parts[0];
            selectedCourse = parts[1];
            await SendGroups(chatId, selectedFaculty, selectedCourse);
        }
        else if (data.StartsWith("group_"))
        {
            string[] parts = data.Substring(6).Split('_');
            selectedFaculty = parts[0];
            selectedCourse = parts[1];
            selectedGroup = parts[2];
            await UpdateMenuButton(chatId, selectedFaculty, selectedCourse, selectedGroup);
            await AskForDaySelection(chatId);
        }
        else if (data.StartsWith("back_"))
        {
            string[] parts = data.Split('_');
            if (parts[1] == "faculty")
                await SendFaculties(chatId);
            else if (parts[1] == "course")
                await SendCourses(chatId, parts[2]);
        }
    }
}