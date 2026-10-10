var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Fleet_Gateway>("fleetd")
    .WithEnvironment("Fleet__Runtime__AutoStart", "false");

builder.Build().Run();
