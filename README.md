# PurgeHistoryExample
Example which contains issue with purging the history of Azure Durable functions

# How to simulate the issue
1. Run the following command from the root of the project to build the docker container.
```bat
docker build . -f "src\PurgeHistoryExample\Dockerfile" -t purgehistoryexample
```

2. Run the container using the following command (replace the required parameters)
```bat
docker run --name purgehistoryexample -p 8080:8080 -e "AzureWebJobsStorage=$env:CONNECTION_STRING" --rm purgehistoryexample
```