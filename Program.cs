using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using PhoneNumbers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// قراءة متغيرات البيئة
var configuration = builder.Configuration;
string botToken = configuration["BOT_TOKEN"] ?? "";
string databaseUrl = configuration["DATABASE_URL"] ?? ""; // Render provides this when you add a Postgres DB

if (string.IsNullOrWhiteSpace(botToken))
{
    Console.WriteLine("ERROR: BOT_TOKEN environment variable is required.");
}

// Configure EF Core with Postgres (convert DATABASE_URL if it's in postgres:// format)
string connStr = ConvertRenderDatabaseUrlToConnectionString(databaseUrl);
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connStr));

// Telegram client (singleton)
builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(botToken));

var app = builder.Build();

// ensure db created and seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated(); // for simplicity; for production use migrations
    if (!db.Contacts.Any())
    {
        db.Contacts.AddRange(
            new Contact { Phone = "+963933123456", Name = "محمد أحمد", Type = "mobile" },
            new Contact { Phone = "+963112345678", Name = "شركة الاتصالات", Type = "landline" },
            new Contact { Phone = "+963944123456", Name = "فاطمة علي", Type = "mobile" },
            new Contact { Phone = "+963113456789", Name = "المستشفى الوطني", Type = "landline" }
        );
        db.SaveChanges();
    }
}

// Health check (Render may use this)
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Webhook endpoint: /webhook/{token}
app.MapPost("/webhook/{token}", async ([FromRoute] string token, HttpRequest request, ITelegramBotClient botClient, AppDbContext db) =>
{
    // أمن بسيط: تحقق أن {token} يطابق BOT_TOKEN (أو جزء منه)
    if (token != (botClient as TelegramBotClient)?.Token)
    {
        return Results.Unauthorized();
    }

    using var sr = new StreamReader(request.Body);
    var body = await sr.ReadToEndAsync();

    // سجل الطلب للـ debugging (يمكن تعطيله لاحقاً)
    Console.WriteLine("Webhook body: " + body);

    // حوّل JSON إلى Update object
    var update = JsonSerializer.Deserialize<Telegram.Bot.Types.Update>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (update == null) return Results.Ok();

    if (update.Message is not null && update.Message.Text is not null)
    {
        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();

        string response = ProcessPhoneNumber(text, db);
        await botClient.SendTextMessageAsync(chatId, response, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
    }

    return Results.Ok();
});

// Start the app on the port Render expects
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

app.Run();


// ------------------ Helper functions & classes ------------------

static string ConvertRenderDatabaseUrlToConnectionString(string databaseUrl)
{
    // Render gives DATABASE_URL like: postgres://user:pass@host:5432/dbname
    if (string.IsNullOrWhiteSpace(databaseUrl)) return databaseUrl;
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':');
    var builder = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port,
        Username = userInfo[0],
        Password = userInfo.Length > 1 ? userInfo[1] : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = Npgsql.SslMode.Prefer,
        TrustServerCertificate = true
    };
    return builder.ConnectionString;
}

static string ProcessPhoneNumber(string phoneNumber, AppDbContext db)
{
    try
    {
        string formatted = FormatPhoneNumber(phoneNumber);
        // تحقق إذا سورياً
        PhoneNumberUtil phoneUtil = PhoneNumberUtil.GetInstance();
        try
        {
            var parsed = phoneUtil.Parse(formatted, "SY");
            if (!phoneUtil.IsValidNumber(parsed) || phoneUtil.GetRegionCodeForNumber(parsed) != "SY")
                return "⚠ هذا الرقم ليس رقماً سورياً. ارسل رقم سوري صحيح.";
        }
        catch
        {
            return "⚠ لم أستطع تحليل الرقم. ارسله بصيغة مثل: 0933123456 أو +963933123456";
        }

        // تحقق في DB
        var contact = db.Contacts.FirstOrDefault(c => c.Phone == formatted);
        if (contact != null)
        {
            string phoneType = contact.Type == "mobile" ? "محمول" : "أرضي";
            return $"✅ الاسم: {contact.Name}\n📞 النوع: {phoneType}\n🇸🇾 الرقم: {formatted}";
        }

        // لم نجده — نحدّد نوعه فقط
        var num = phoneUtil.Parse(formatted, "SY");
        var numberType = phoneUtil.GetNumberType(num);
        string typeArabic = numberType == PhoneNumbers.PhoneNumberType.MOBILE ? "محمول" : "أرضي";
        return $"📞 الرقم: {formatted}\n📱 النوع: {typeArabic}\nℹ لم يتم العثور على الاسم في قاعدة البيانات";
    }
    catch (Exception ex)
    {
        return $"❌ خطأ داخلي: {ex.Message}";
    }
}

static string FormatPhoneNumber(string phoneNumber)
{
    string cleaned = phoneNumber.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
    if (cleaned.StartsWith("09") && cleaned.Length == 10) return "+963" + cleaned.Substring(1);
    if (cleaned.StartsWith("9") && cleaned.Length == 9) return "+963" + cleaned;
    if (cleaned.StartsWith("0") && cleaned.Length == 10 && cleaned[1] != '9') return "+963" + cleaned.Substring(1);
    if (cleaned.StartsWith("+963")) return cleaned;
    if (cleaned.StartsWith("963")) return "+" + cleaned;
    return cleaned;
}


// EF Core model & DbContext
public class Contact
{
    public int Id { get; set; }
    public string Phone { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Type { get; set; } = default!; // "mobile" or "landline"
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }
    public DbSet<Contact> Contacts { get; set; } = default!;
}
