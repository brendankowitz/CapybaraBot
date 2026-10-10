var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Capybara_Gateway>("capybarad")
    .WithEnvironment("Capybara__Runtime__AutoStart", "false");

builder.Build().Run();
