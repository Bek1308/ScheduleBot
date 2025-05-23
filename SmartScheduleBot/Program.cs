using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ClosedXML.Excel;

// Foydalanuvchi holatini saqlash uchun sinf
public class UserState
{
    public string SelectedFaculty { get; set; } = "";
    public string SelectedCourse { get; set; } = "";
    public string SelectedGroup { get; set; } = "";
    public string SelectedTeacherCode { get; set; } = null;
    public bool IsTeacherMode { get; set; } = false;
}

class Program
{
    private static TelegramBotClient botClient;
    private static TelegramBotClient targetBotClient;
    private static string targetBotToken = "TargetBotToken";
    private static long targetChatId = 6010438305;
    private static string baseUrl = "https://smartschedule-k0ex.onrender.com";
    private static readonly ConcurrentDictionary<long, UserState> userStates = new ConcurrentDictionary<long, UserState>();
    private static readonly ConcurrentDictionary<long, bool> awaitingAdminCode = new ConcurrentDictionary<long, bool>();
    private static readonly ConcurrentDictionary<long, bool> uniqueUsers = new ConcurrentDictionary<long, bool>();
    private static readonly ConcurrentDictionary<long, DateTime> activeUsers = new ConcurrentDictionary<long, DateTime>();
    private static readonly ConcurrentDictionary<long, string> userUsernames = new ConcurrentDictionary<long, string>();
    private static int statsMessageId = 0;
    private static readonly string dataFilePath = "user_data.txt";
    private static readonly object fileLock = new object();

    // Fayldan ma’lumotlarni yuklash
    private static void LoadUserData()
    {
        try
        {
            lock (fileLock)
            {
                if (File.Exists(dataFilePath))
                {
                    var lines = File.ReadAllLines(dataFilePath);
                    bool readingUniqueUsers = false;
                    bool readingUserUsernames = false;

                    foreach (var line in lines)
                    {
                        if (line.Trim() == "uniqueUsers:")
                        {
                            readingUniqueUsers = true;
                            readingUserUsernames = false;
                            continue;
                        }
                        else if (line.Trim() == "userUsernames:")
                        {
                            readingUniqueUsers = false;
                            readingUserUsernames = true;
                            continue;
                        }

                        if (readingUniqueUsers && long.TryParse(line.Trim(), out long userId))
                        {
                            uniqueUsers.TryAdd(userId, true);
                        }
                        else if (readingUserUsernames)
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 2 && long.TryParse(parts[0].Trim(), out long id))
                            {
                                string username = string.Join(":", parts[1..]).Trim();
                                userUsernames.TryAdd(id, username);
                            }
                        }
                    }
                    Console.WriteLine("✅ Fayldan foydalanuvchi ma’lumotlari yuklandi.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fayldan ma’lumot yuklashda xato: {ex.Message}");
        }
    }

    // Ma’lumotlarni faylga saqlash
    private static void SaveUserData()
    {
        try
        {
            lock (fileLock)
            {
                using (var writer = new StreamWriter(dataFilePath))
                {
                    writer.WriteLine("uniqueUsers:");
                    foreach (var userId in uniqueUsers.Keys)
                    {
                        writer.WriteLine(userId);
                    }
                    writer.WriteLine("userUsernames:");
                    foreach (var kvp in userUsernames)
                    {
                        writer.WriteLine($"{kvp.Key}:{kvp.Value}");
                    }
                }
                Console.WriteLine("✅ Foydalanuvchi ma’lumotlari faylga saqlandi.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Faylga ma’lumot saqlashda xato: {ex.Message}");
        }
    }

    public static async Task Main(string[] args)
    {
        var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "Token";
        botClient = new TelegramBotClient(botToken);
        targetBotClient = new TelegramBotClient(targetBotToken);

        LoadUserData();

        var me = await botClient.GetMeAsync();
        Console.WriteLine($"✅ Бот {me.FirstName} ба кор андохта шуд.");

        await botClient.DeleteWebhookAsync();
        int offset = 0;

        await SendInitialStatsMessage();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await UpdateStatsMessage();
                    SaveUserData();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Statistikani yangilashda xato: {ex.Message}");
                }
                await Task.Delay(30000);
            }
        });

        while (true)
        {
            try
            {
                var updates = await botClient.GetUpdatesAsync(offset, timeout: 30);
                foreach (var update in updates)
                {
                    _ = Task.Run(() => HandleUpdateAsync(botClient, update, CancellationToken.None));
                    offset = update.Id + 1;
                }
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
            {
                Console.WriteLine($"❌ Forbidden xatosi pollingda: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Polling xatoligi: {ex.Message}");
                await Task.Delay(5000);
            }
        }
    }

    private static async Task SendInitialStatsMessage()
    {
        try
        {
            int totalUsers = uniqueUsers.Count;
            int activeUsersCount = activeUsers.Count(u => (DateTime.Now - u.Value).TotalMinutes <= 5);
            string statsMessage = $"📊 Статистика:\n" +
                                 $"Общее количество пользователей: {totalUsers}\n" +
                                 $"Онлайн пользователи: {activeUsersCount}\n" +
                                 $"Список активных пользователей:\n{GetActiveUsersList()}";

            var sentMessage = await targetBotClient.SendTextMessageAsync(targetChatId, statsMessage);
            statsMessageId = sentMessage.MessageId;
            Console.WriteLine("✅ Boshlang‘ich statistika xabari yuborildi.");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendInitialStatsMessage): {ex.Message}");
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
            string statsMessage = $"📊Статистика:\n" +
                                 $"Общее количество пользователей : {totalUsers}\n" +
                                 $"Онлайн пользователи: {activeUsersCount}\n" +
                                 $"Список активных пользователей:\n{GetActiveUsersList()}";

            await targetBotClient.EditMessageTextAsync(targetChatId, statsMessageId, statsMessage);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (UpdateStatsMessage): {ex.Message}");
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("message is not modified")) return;
            Console.WriteLine($"❌ Хатогӣ (UpdateStatsMessage): {ex.Message}");
        }
    }

    private static string GetActiveUsersList()
    {
        string result = "";
        foreach (var user in activeUsers)
        {
            if ((DateTime.Now - user.Value).TotalMinutes <= 5)
            {
                string username = userUsernames.GetValueOrDefault(user.Key, "Noma’lum");
                result += $"ID: {user.Key}, Username: {username}\n";
            }
        }
        return result.Length > 0 ? result : "Фаол корбарлар йўқ.";
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message?.Text != null)
            {
                long chatId = update.Message.Chat.Id;
                long userId = update.Message.From.Id;
                string messageText = update.Message.Text;

                uniqueUsers.TryAdd(userId, true);
                activeUsers.AddOrUpdate(userId, DateTime.Now, (k, v) => DateTime.Now);
                userUsernames.AddOrUpdate(userId, update.Message.From.Username ?? $"{update.Message.From.FirstName} {update.Message.From.LastName}".Trim(), (k, v) => v);

                var state = userStates.GetOrAdd(chatId, _ => new UserState());

                if (awaitingAdminCode.GetValueOrDefault(chatId, false))
                {
                    if (messageText == "13082003")
                    {
                        awaitingAdminCode.TryRemove(chatId, out _);
                        Console.WriteLine($"📩 Корбар {userId} коди дурусти админро ворид кард.");
                        await ExportTeachersList(chatId);
                    }
                    else
                    {
                        awaitingAdminCode.TryRemove(chatId, out _);
                        await botClient.SendTextMessageAsync(chatId, "❌ Код нодуруст аст! Лутфан дубора бо фармони /admin ворид кунед.");
                    }
                    return;
                }

                switch (messageText)
                {
                    case "/start":
                        Console.WriteLine($"📩 Корбар {userId} фармони /start-ро фиристод.");
                        awaitingAdminCode.TryRemove(chatId, out _);
                        await botClient.SetChatMenuButtonAsync(chatId, new MenuButtonDefault());
                        bool isSubscribed = await IsUserSubscribed(userId);
                        if (isSubscribed)
                        {
                            userStates.AddOrUpdate(chatId, new UserState(), (k, v) => new UserState());
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
                        awaitingAdminCode.AddOrUpdate(chatId, true, (k, v) => true);
                        await botClient.SendTextMessageAsync(chatId, "🔑 Лутфан коди админро ворид кунед:");
                        Console.WriteLine($"📩 Корбар {userId} фармони /admin-ро фиристод.");
                        break;

                    default:
                        if (state.IsTeacherMode && state.SelectedTeacherCode == null && !messageText.StartsWith("/"))
                        {
                            await HandleTeacherCodeInput(chatId, messageText);
                        }
                        else if (state.IsTeacherMode && state.SelectedTeacherCode != null && !messageText.StartsWith("/"))
                        {
                            await HandleDaySelection(chatId, messageText);
                        }
                        else if (!state.IsTeacherMode && !messageText.StartsWith("/"))
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
                uniqueUsers.TryAdd(userId, true);
                activeUsers.AddOrUpdate(userId, DateTime.Now, (k, v) => DateTime.Now);
                userUsernames.AddOrUpdate(userId, update.CallbackQuery.From.Username ?? $"{update.CallbackQuery.From.FirstName} {update.CallbackQuery.From.LastName}".Trim(), (k, v) => v);
                userStates.GetOrAdd(update.CallbackQuery.Message.Chat.Id, _ => new UserState());
                await HandleCallbackQuery(update.CallbackQuery);
            }
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi foydalanuvchi {update.Message?.From?.Id}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ HandleUpdateAsync xatosi: {ex.Message}");
        }
    }

    private static async Task<bool> IsUserSubscribed(long userId)
    {
        try
        {
            var chatMember = await botClient.GetChatMemberAsync("@Career1ink", userId);
            return chatMember.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (IsUserSubscribed) foydalanuvchi {userId}: {ex.Message}");
            return false;
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
                   "🎉 Хуш омадед ба *SmartScheduleBot*! Ман ба шумо дар ёфтани осон ва зуд жадвали дарсī кӯмак мерасонам.\n\n" +
                   "📚 *Имкониятҳои ман:*\n" +
                   "- Барои донишҷӯён жадвали дарсī аз рӯи факулта, курс ва гурӯҳ.\n" +
                   "- Барои муаллимон жадвали дарс тавассути коди махсус.\n" +
                   "- Жадвали дақиқ барои ҳар рӯз ва дидани жадвали пурра тавассути тугмаи веб.\n\n" +
                   "🔧 *Фармонҳои мавҷуд:*\n" +
                   "/start - Оғози дубораи бот\n" +
                   "/help - Кӯмак ва иттилоот\n" +
                   "/admin - Боргирии рӯйхати муаллимон барои админҳо\n\n" +
                   "📩 *Барои пешниҳод ва талабҳо:* Ба @future1308 нависед!";

        try
        {
            await botClient.SendTextMessageAsync(chatId, welcomeMessage, parseMode: ParseMode.Markdown);
            Console.WriteLine($"📩 Ба корбар {chatId} паёми хуш омадед фиристода шуд.");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendWelcomeMessage) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогӣ (SendWelcomeMessage): {ex.Message}");
        }
    }

    private static async Task SendHelpMessage(long chatId)
    {
        string helpMessage =
            "ℹ️ *Ёрдамчии SmartScheduleBot*\n\n" +
            "Ман ба шумо дар ёфтани жадвали дарсī осон кӯмак мерасонам:\n" +
            "1. Агар донишҷӯ бошед: Факулта, курс ва гурӯҳатонро интихоб кунед.\n" +
            "2. Агар муаллим бошед: Коди махсусатонро ворид кунед.\n" +
            "3. Барои жадвали рӯзона рӯзро интихоб кунед ё тавассути тугмаи *Пурра* жадвали комилро бинед.\n\n" +
            "🔧 *Фармонҳо:*\n" +
            "/start - Оғоз\n" +
            "/help - Ин кӯмак\n" +
            "/admin - Рӯйхати муаллимон (фақат барои админҳо)\n\n" +
            "📩 Агар савол ё пешниҳод дошта бошед, ба @future1308 муроҷиат кунед!";

        try
        {
            await botClient.SendTextMessageAsync(chatId, helpMessage, parseMode: ParseMode.Markdown);
            Console.WriteLine($"📩 Ба корбар {chatId} паёми кӯмак фиристода шуд.");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendHelpMessage) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогӣ (SendHelpMessage): {ex.Message}");
        }
    }

    private static async Task SendUserTypeSelection(long chatId)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("👨‍🎓 Донишҷӯ", "student") },
            new[] { InlineKeyboardButton.WithCallbackData("👨‍🏫 Муаллим", "teacher") }
        });

        try
        {
            await botClient.SendTextMessageAsync(chatId, "📌 Ба кадом сифат идома додан мехоҳед?", replyMarkup: inlineKeyboard);
            Console.WriteLine($"📩 Ба корбар {chatId} савол оиди интихоби сифат фиристода шуд.");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendUserTypeSelection) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогӣ (SendUserTypeSelection): {ex.Message}");
        }
    }

    private static async Task SendSubscriptionPrompt(long chatId)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("📢 Ба канал обуна шавед", "https://t.me/Career1ink") },
            new[] { InlineKeyboardButton.WithCallbackData("✅ Обунаро санҷед", "check_subscription") }
        });

        try
        {
            await botClient.SendTextMessageAsync(chatId, "❌ Лутфан аввал ба канал обуна шавед, баъд аз бот истифода баред:", replyMarkup: inlineKeyboard);
            Console.WriteLine($"📢 Ба корбар {chatId} саволи обунашавī фиристода шуд.");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendSubscriptionPrompt) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогӣ (SendSubscriptionPrompt): {ex.Message}");
        }
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendFaculties) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогī: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани факултаҳо хатогī: {ex.Message}");
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendCourses) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогī: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани курсҳо хатогī: {ex.Message}");
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (SendGroups) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогī: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани гурӯҳҳо хатогī: {ex.Message}");
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (UpdateMenuButton) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Дар нав кардани тугмаи Web App хатогī: {ex.Message}");
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (UpdateTeacherMenuButton) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Дар нав кардани тугмаи Web App хатогī: {ex.Message}");
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

        try
        {
            await botClient.SendTextMessageAsync(chatId, message, replyMarkup: keyboard);
            Console.WriteLine($"📅 Ба корбар {chatId} саволи интихоби рӯз фиристода шуд (муаллим: {isTeacher}).");
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (AskForDaySelection) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогī (AskForDaySelection): {ex.Message}");
        }
    }

    private static async Task HandleDaySelection(long chatId, string selectedDay)
    {
        var state = userStates.GetOrAdd(chatId, _ => new UserState());

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
        if (state.IsTeacherMode && state.SelectedTeacherCode != null)
        {
            webAppUrl = $"{baseUrl}/get_teacher_day/{state.SelectedTeacherCode}?day={Uri.EscapeDataString(dayToFetch)}";
            Console.WriteLine($"📤 Супориши жадвали рӯзона барои муаллим: {webAppUrl}");
        }
        else
        {
            if (string.IsNullOrEmpty(state.SelectedFaculty) || string.IsNullOrEmpty(state.SelectedCourse) || string.IsNullOrEmpty(state.SelectedGroup))
            {
                try
                {
                    await botClient.SendTextMessageAsync(chatId, "❌ Лутфан аввал факулта, курс ва гурӯҳро интихоб кунед!");
                    await SendUserTypeSelection(chatId);
                }
                catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
                {
                    Console.WriteLine($"❌ Forbidden xatosi (HandleDaySelection) foydalanuvchi {chatId}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Хатогī (HandleDaySelection): {ex.Message}");
                }
                return;
            }
            webAppUrl = $"{baseUrl}/get_day/{state.SelectedFaculty}/{state.SelectedCourse}/{state.SelectedGroup}?day={Uri.EscapeDataString(dayToFetch)}";
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (HandleDaySelection) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогī: {ex.Message}");
            Console.WriteLine($"❌ Дар гирифтани жадвал хатогī: {ex.Message}");
        }
    }

    private static async Task HandleTeacherCodeInput(long chatId, string code)
    {
        var state = userStates.GetOrAdd(chatId, _ => new UserState());

        try
        {
            using var client = new HttpClient();
            var content = new StringContent(JsonSerializer.Serialize(new { teacher_code = code }), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{baseUrl}/check_teacher_code", content);
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(await response.Content.ReadAsStringAsync());

            if (result["status"] == "success")
            {
                state.SelectedTeacherCode = code;
                state.IsTeacherMode = true;
                Console.WriteLine($"✅ Коди муаллим насб шуд: {state.SelectedTeacherCode}, isTeacherMode: {state.IsTeacherMode}");
                await botClient.SendTextMessageAsync(chatId, $"🎉 Хуш омадед, Устод {result["teacher_name"]}!");
                await UpdateTeacherMenuButton(chatId, code);
                await AskForDaySelection(chatId, true);
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "❌ Код нодуруст аст! Лутфан дубора код ворид кунед.");
            }
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (HandleTeacherCodeInput) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогī: {ex.Message}");
            Console.WriteLine($"❌ Дар санҷиши код хатогī: {ex.Message}");
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
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (ExportTeachersList) foydalanuvchi {chatId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            await botClient.SendTextMessageAsync(chatId, $"❌ Хатогī: {ex.Message}");
            Console.WriteLine($"❌ Дар содироти рӯйхати муаллимон хатогī: {ex.Message}");
        }
    }

    private static async Task HandleCallbackQuery(CallbackQuery callbackQuery)
    {
        long chatId = callbackQuery.Message.Chat.Id;
        int messageId = callbackQuery.Message.MessageId;
        string data = callbackQuery.Data;
        Console.WriteLine($"📩 Callback қабул шуд: {data}");

        var state = userStates.GetOrAdd(chatId, _ => new UserState());

        try
        {
            await botClient.DeleteMessageAsync(chatId, messageId);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden"))
        {
            Console.WriteLine($"❌ Forbidden xatosi (HandleCallbackQuery) foydalanuvchi {chatId}: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Хатогӣ (HandleCallbackQuery): {ex.Message}");
        }

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
            state.IsTeacherMode = false;
            state.SelectedTeacherCode = null;
            state.SelectedFaculty = "";
            state.SelectedCourse = "";
            state.SelectedGroup = "";
            await SendFaculties(chatId);
        }
        else if (data == "teacher")
        {
            state.IsTeacherMode = true;
            state.SelectedFaculty = "";
            state.SelectedCourse = "";
            state.SelectedGroup = "";
            state.SelectedTeacherCode = null;
            await botClient.SendTextMessageAsync(chatId, "🔑 Лутфан коди махсуси худро ворид кунед:");
        }
        else if (data.StartsWith("faculty_"))
        {
            state.SelectedFaculty = data.Substring(8);
            await SendCourses(chatId, state.SelectedFaculty);
        }
        else if (data.StartsWith("course_"))
        {
            string[] parts = data.Substring(7).Split('_');
            state.SelectedFaculty = parts[0];
            state.SelectedCourse = parts[1];
            await SendGroups(chatId, state.SelectedFaculty, state.SelectedCourse);
        }
        else if (data.StartsWith("group_"))
        {
            string[] parts = data.Substring(6).Split('_');
            state.SelectedFaculty = parts[0];
            state.SelectedCourse = parts[1];
            state.SelectedGroup = parts[2];
            await UpdateMenuButton(chatId, state.SelectedFaculty, state.SelectedCourse, state.SelectedGroup);
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