using Aspire.Azure.AI.OpenAI;
using ChromaDB.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Products.Endpoints;
using Products.Memory;
using Products.Models;
using ChromaDB.Client;


var builder = WebApplication.CreateBuilder(args);

// Disable Globalization Invariant Mode
Environment.SetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "false");

// add aspire service defaults
builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

// Add DbContext service
builder.AddSqlServerDbContext<Context>("sqldb");

// in dev scenarios add "openai" user secrets info, check the official doc for the necessary steps
var azureOpenAiClientName = "openai";
builder.AddAzureOpenAIClient(azureOpenAiClientName);

// get azure openai client and create Chat client from aspire hosting configuration
builder.Services.AddSingleton<ChatClient>(serviceProvider =>
{
    var chatDeploymentName = "gpt-4o-mini";
    var logger = serviceProvider.GetService<ILogger<Program>>()!;
    logger.LogInformation($"Chat client configuration, modelId: {chatDeploymentName}");
    ChatClient chatClient = null;
    try
    {
        OpenAIClient client = serviceProvider.GetRequiredService<OpenAIClient>();
        chatClient = client.GetChatClient(chatDeploymentName);
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating embeddings client");
    }
    return chatClient;
});

// get azure openai client and create embedding client from aspire hosting configuration
builder.Services.AddSingleton<EmbeddingClient>(serviceProvider =>
{
    var embeddingsDeploymentName = "text-embedding-ada-002";
    var logger = serviceProvider.GetService<ILogger<Program>>()!;
    logger.LogInformation($"Embeddings client configuration, modelId: {embeddingsDeploymentName}");
    EmbeddingClient embeddingsClient = null;
    try
    {
        OpenAIClient client = serviceProvider.GetRequiredService<OpenAIClient>();
        embeddingsClient = client.GetEmbeddingClient(embeddingsDeploymentName);
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating embeddings client");
    }
    return embeddingsClient;
});

// get the ChromaDB Collection Client
builder.Services.AddSingleton<ChromaCollectionClient>(serviceProvider =>
{
    var logger = serviceProvider.GetService<ILogger<Program>>()!;
    var config = serviceProvider.GetService<IConfiguration>()!;
    ChromaCollectionClient chromaCollectionClient = null;
    try
    {
        // get chromaDB service Uri from configuration
        var chromaDbService = config.GetSection("services:chroma:chromaendpoint:0");
        var chromaDbUri = chromaDbService.Value;
        logger.LogInformation($"ChromaDB client configuration, key: {chromaDbService.Key}");
        logger.LogInformation($"ChromaDB client configuration, value: {chromaDbService.Value}");

        if (!string.IsNullOrEmpty(chromaDbUri) && !chromaDbUri.EndsWith("/api/v1/"))
        {
            logger.LogInformation("ChromaDB connection string does not end with /api/v1/, adding it");
            chromaDbUri += "/api/v1/";
        }
        logger.LogInformation($"ChromaDB client uri: {chromaDbUri}");


        // 
        var configOptions = new ChromaConfigurationOptions(uri: chromaDbUri);
        var httpChromaClient = new HttpClient();
        var chromaClient = new ChromaClient(configOptions, httpChromaClient);
        var collection = chromaClient.GetOrCreateCollection("products").Result;
        chromaCollectionClient = new ChromaCollectionClient(collection, configOptions, httpChromaClient);
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating chromaDB client");
    }
    return chromaCollectionClient;
});

builder.Services.AddSingleton<IConfiguration>(sp =>
{
    return builder.Configuration;
});

// add memory context
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetService<ILogger<Program>>();
    logger.LogInformation("Creating memory context");
    return new MemoryContext(
        logger,
        sp.GetService<ChatClient>(), 
        sp.GetService<EmbeddingClient>(),
        sp.GetService<ChromaCollectionClient>());
});

// Add services to the container.
var app = builder.Build();

// aspire map default endpoints
app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapProductEndpoints();

app.UseStaticFiles();

// log Azure OpenAI resources
app.Logger.LogInformation($"Azure OpenAI resources\n >> OpenAI Client Name: {azureOpenAiClientName}");
AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);

// get from builder.Configuration the service named chromadb
var chromaDbService = builder.Configuration.GetSection("services:chroma:chromaendpoint:0");
app.Logger.LogInformation($"ChromaDB client configuration, key: {chromaDbService.Value}");
app.Logger.LogInformation($"ChromaDB client configuration, value: {chromaDbService.Value}");

// manage db
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<Context>();
    try
    {
        app.Logger.LogInformation("Ensure database created");
        context.Database.EnsureCreated();
    }
    catch (Exception exc)
    {
        app.Logger.LogError(exc, "Error creating database");
    }
    DbInitializer.Initialize(context);
}

app.Run();
