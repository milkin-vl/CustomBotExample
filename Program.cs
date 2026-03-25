// ============================================================================
//  CustomBotExample — пример реализации собственного бота для приёма
//  уведомлений от Обновлятора-1С.
//
//  Что делает это приложение:
//    1. Запускает HTTP-сервер на порту, указанном в константе DefaultPort.
//    2. Принимает POST-запросы на два эндпоинта:
//       • /sendMessage — приём текстового сообщения (JSON с полями subject и body).
//       • /sendFile    — приём файла (multipart/form-data, поле document).
//    3. Проверяет авторизацию по токену из заголовка Authorization: Bearer <token>.
//    4. Сохраняет полученные данные в подпапку ReceivedData рядом с исполняемым файлом.
//
//  Для быстрого старта достаточно:
//    1. Убедиться, что установлен .NET 8 SDK.
//    2. Выполнить: dotnet run
//    3. В настройках обновлятора указать URL-ы эндпоинтов и токен.
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------
//  НАСТРОЙКИ — измените под свои нужды
// ---------------------------------------------------------------------------

/// <summary>
/// Порт, на котором будет запущен HTTP-сервер.
/// Выбран нестандартный порт, чтобы не конфликтовать с другими приложениями.
/// </summary>
const int DefaultPort = 9099;

/// <summary>
/// Токен авторизации. Запросы без корректного токена будут отклонены (401).
/// Этот же токен нужно прописать в настройках CustomBot в обновляторе.
/// </summary>
const string Token = "MY-SECRET-TOKEN-CHANGE-ME";

// ---------------------------------------------------------------------------
//  Папка для сохранения полученных данных
// ---------------------------------------------------------------------------

var receivedDataPath = Path.Combine(AppContext.BaseDirectory, "ReceivedData");
Directory.CreateDirectory(receivedDataPath);

// ---------------------------------------------------------------------------
//  Настройки сериализации JSON (pretty-print)
// ---------------------------------------------------------------------------

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
};

// ---------------------------------------------------------------------------
//  Создание и настройка веб-приложения
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// Отключаем стандартное логирование ASP.NET, чтобы вывод был чистым
builder.Logging.ClearProviders();

// Настраиваем Kestrel на нужный порт
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(DefaultPort);
});

var app = builder.Build();

// ---------------------------------------------------------------------------
//  Вывод стартовой информации
// ---------------------------------------------------------------------------

Console.WriteLine("============================================================");
Console.WriteLine("  CustomBotExample — сервер для приёма уведомлений");
Console.WriteLine("============================================================");
Console.WriteLine();
Console.WriteLine($"  Порт:              {DefaultPort}");
Console.WriteLine($"  Папка данных:      {receivedDataPath}");
Console.WriteLine($"  Эндпоинт сообщений: POST http://localhost:{DefaultPort}/sendMessage");
Console.WriteLine($"  Эндпоинт файлов:    POST http://localhost:{DefaultPort}/sendFile");
Console.WriteLine();
Console.WriteLine("  Ожидание входящих запросов... (Ctrl+C для остановки)");
Console.WriteLine("============================================================");
Console.WriteLine();

// ---------------------------------------------------------------------------
//  Эндпоинт: приём текстового сообщения
//  POST /sendMessage?chat_id=...&batch_id=...
//  Тело: JSON { "subject": "...", "body": "..." }
//  Заголовок: Authorization: Bearer <token>
// ---------------------------------------------------------------------------

app.MapPost("/sendMessage", async (HttpContext context) =>
{
    // --- Проверка авторизации ---
    if (!CheckAuthorization(context))
    {
        return Results.Json(new { ok = false, error = "Неверный токен авторизации." }, statusCode: 401);
    }

    // --- Получение обязательных query-параметров ---
    var chatId = context.Request.Query["chat_id"].FirstOrDefault();
    var batchId = context.Request.Query["batch_id"].FirstOrDefault();

    if (string.IsNullOrEmpty(chatId))
    {
        return Results.Json(new { ok = false, error = "Отсутствует обязательный параметр chat_id." }, statusCode: 400);
    }

    if (string.IsNullOrEmpty(batchId))
    {
        return Results.Json(new { ok = false, error = "Отсутствует обязательный параметр batch_id." }, statusCode: 400);
    }

    // --- Чтение тела запроса ---
    string rawBody;
    using (var reader = new StreamReader(context.Request.Body))
    {
        rawBody = await reader.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(rawBody))
    {
        return Results.Json(new { ok = false, error = "Тело запроса пустое." }, statusCode: 400);
    }

    // --- Создание подпапки ---
    // Формат: yyyy-MM-dd_HHmmssfff_<batch_id>_message
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmssfff");
    var safeBatchId = SanitizeForPath(batchId);
    var folderName = $"{timestamp}_{safeBatchId}_message";
    var folderPath = Path.Combine(receivedDataPath, folderName);
    Directory.CreateDirectory(folderPath);

    // --- Сохранение тела сообщения (pretty-print JSON) ---
    try
    {
        // Парсим и пересериализуем JSON с форматированием
        var jsonDoc = JsonDocument.Parse(rawBody);
        var prettyJson = JsonSerializer.Serialize(jsonDoc, jsonOptions);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "message.json"), prettyJson);
    }
    catch (JsonException)
    {
        // Если тело не является валидным JSON — сохраняем как есть
        await File.WriteAllTextAsync(Path.Combine(folderPath, "message.json"), rawBody);
    }

    // --- Сохранение query-параметров ---
    var queryParams = new { chat_id = chatId, batch_id = batchId };
    var paramsJson = JsonSerializer.Serialize(queryParams, jsonOptions);
    await File.WriteAllTextAsync(Path.Combine(folderPath, "params.json"), paramsJson);

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Сообщение сохранено: {folderName}");
    return Results.Json(new { ok = true });
});

// ---------------------------------------------------------------------------
//  Эндпоинт: приём файла
//  POST /sendFile?chat_id=...&batch_id=...
//  Тело: multipart/form-data, поле document
//  Заголовок: Authorization: Bearer <token>
// ---------------------------------------------------------------------------

app.MapPost("/sendFile", async (HttpContext context) =>
{
    // --- Проверка авторизации ---
    if (!CheckAuthorization(context))
    {
        return Results.Json(new { ok = false, error = "Неверный токен авторизации." }, statusCode: 401);
    }

    // --- Получение обязательных query-параметров ---
    var chatId = context.Request.Query["chat_id"].FirstOrDefault();
    var batchId = context.Request.Query["batch_id"].FirstOrDefault();

    if (string.IsNullOrEmpty(chatId))
    {
        return Results.Json(new { ok = false, error = "Отсутствует обязательный параметр chat_id." }, statusCode: 400);
    }

    if (string.IsNullOrEmpty(batchId))
    {
        return Results.Json(new { ok = false, error = "Отсутствует обязательный параметр batch_id." }, statusCode: 400);
    }

    // --- Проверка, что запрос содержит форму (multipart) ---
    if (!context.Request.HasFormContentType)
    {
        return Results.Json(new { ok = false, error = "Ожидается multipart/form-data." }, statusCode: 400);
    }

    // --- Получение файла из поля document ---
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("document");

    if (file == null)
    {
        return Results.Json(new { ok = false, error = "Файл не найден. Ожидается поле document." }, statusCode: 400);
    }

    if (string.IsNullOrEmpty(file.FileName))
    {
        return Results.Json(new { ok = false, error = "Имя файла отсутствует." }, statusCode: 400);
    }

    // --- Создание подпапки ---
    // Формат: yyyy-MM-dd_HHmmssfff_<batch_id>_file
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmssfff");
    var safeBatchId = SanitizeForPath(batchId);
    var folderName = $"{timestamp}_{safeBatchId}_file";
    var folderPath = Path.Combine(receivedDataPath, folderName);
    Directory.CreateDirectory(folderPath);

    // --- Сохранение файла с оригинальным именем ---
    var safeFileName = SanitizeForPath(file.FileName);
    var filePath = Path.Combine(folderPath, safeFileName);

    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    // --- Сохранение query-параметров ---
    var queryParams = new { chat_id = chatId, batch_id = batchId };
    var paramsJson = JsonSerializer.Serialize(queryParams, jsonOptions);
    await File.WriteAllTextAsync(Path.Combine(folderPath, "params.json"), paramsJson);

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Файл сохранён: {folderName}/{safeFileName}");
    return Results.Json(new { ok = true });
});

// ---------------------------------------------------------------------------
//  Запуск приложения
// ---------------------------------------------------------------------------

app.Run();

// ===========================================================================
//  Вспомогательные методы
// ===========================================================================

/// <summary>
/// Проверяет заголовок Authorization: Bearer &lt;token&gt;.
/// Возвращает true, если токен совпадает с константой Token.
/// </summary>
bool CheckAuthorization(HttpContext context)
{
    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrEmpty(authHeader))
    {
        return false;
    }

    // Ожидаемый формат: "Bearer <token>"
    if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var receivedToken = authHeader.Substring("Bearer ".Length).Trim();
    return receivedToken == Token;
}

/// <summary>
/// Удаляет из строки символы, недопустимые в именах файлов и папок.
/// Это гарантирует корректную работу на всех ОС.
/// </summary>
string SanitizeForPath(string input)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var result = new char[input.Length];
    for (int i = 0; i < input.Length; i++)
    {
        result[i] = Array.IndexOf(invalidChars, input[i]) >= 0 ? '_' : input[i];
    }
    return new string(result);
}
