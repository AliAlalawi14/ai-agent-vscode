var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<EvalShop.Calculator>();

var app = builder.Build();
app.MapControllers();
app.Run();
