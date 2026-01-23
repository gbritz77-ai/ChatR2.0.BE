$script = @'
$ErrorActionPreference = "Continue"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Complete AWS Setup for ChatR Backend" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Get Account ID
$accountId = aws sts get-caller-identity --query Account --output text
Write-Host "`nAWS Account ID: $accountId" -ForegroundColor Green

# Configuration
$region = "eu-west-2"

$dbPassword = Read-Host "Enter PostgreSQL password for RDS" -AsSecureString
$dbPasswordPlain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($dbPassword)
)

$jwtKeySecure = Read-Host "Enter JWT signing key (will be stored in AWS Secrets Manager)" -AsSecureString
$jwtKeyPlain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($jwtKeySecure)
)

Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Step 1: Fixing Secrets Manager Policy" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow

$secretsPolicy = @"
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "secretsmanager:GetSecretValue",
      "Resource": [
        "arn:aws:secretsmanager:$region:${accountId}:secret:chatr/db-connection-*",
        "arn:aws:secretsmanager:$region:${accountId}:secret:chatr/jwt-key-*"
      ]
    }
  ]
}
"@

$Utf8NoBomEncoding = New-Object System.Text.UTF8Encoding $False
[System.IO.File]::WriteAllLines("$PWD\secrets-manager-policy.json", $secretsPolicy, $Utf8NoBomEncoding)

aws iam put-role-policy --role-name ecsTaskExecutionRole --policy-name SecretsManagerPolicy --policy-document file://secrets-manager-policy.json

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ Secrets Manager policy fixed" -ForegroundColor Green
}

Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Step 2: Creating AWS Secrets" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow

$rdsEndpoint = "localhost"
$dbConnectionString = "Host=$rdsEndpoint;Port=5432;Database=ChatRDb;Username=postgres;Password=$dbPasswordPlain"

aws secretsmanager create-secret --name chatr/db-connection --description "ChatR Database Connection String" --secret-string $dbConnectionString --region $region 2>$null

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ Database connection secret created" -ForegroundColor Green
} else {
    aws secretsmanager update-secret --secret-id chatr/db-connection --secret-string $dbConnectionString --region $region 2>$null
    Write-Host "✓ Database connection secret updated" -ForegroundColor Green
}

aws secretsmanager create-secret --name chatr/jwt-key --description "ChatR JWT Signing Key" --secret-string $jwtKeyPlain --region $region 2>$null

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ JWT key secret created" -ForegroundColor Green
} else {
    aws secretsmanager update-secret --secret-id chatr/jwt-key --secret-string $jwtKeyPlain --region $region 2>$null
    Write-Host "✓ JWT key secret updated" -ForegroundColor Green
}

Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Step 3: Creating ECR Repository" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow

aws ecr create-repository --repository-name chatr-backend --region $region --image-scanning-configuration scanOnPush=true 2>$null

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ ECR repository created" -ForegroundColor Green
} else {
    Write-Host "✓ ECR repository already exists" -ForegroundColor Green
}

$ecrUri = aws ecr describe-repositories --repository-names chatr-backend --query "repositories[0].repositoryUri" --output text --region $region

Write-Host "ECR URI: $ecrUri" -ForegroundColor Cyan

Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Step 4: Creating ECS Cluster" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow

aws ecs create-cluster --cluster-name chatr-cluster --region $region 2>$null

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ ECS cluster created" -ForegroundColor Green
} else {
    Write-Host "✓ ECS cluster already exists" -ForegroundColor Green
}

Write-Host "`n========================================" -ForegroundColor Yellow
Write-Host "Step 5: Creating Task Definition" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow

$taskDef = @"
{
  "family": "chatr-task",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "256",
  "memory": "512",
  "executionRoleArn": "arn:aws:iam::${accountId}:role/ecsTaskExecutionRole",
  "containerDefinitions": [
    {
      "name": "chatr-api",
      "image": "${ecrUri}:latest",
      "portMappings": [
        {
          "containerPort": 8080,
          "protocol": "tcp"
        }
      ],
      "essential": true,
      "environment": [
        {
          "name": "ASPNETCORE_ENVIRONMENT",
          "value": "Production"
        },
        {
          "name": "ASPNETCORE_URLS",
          "value": "http://+:8080"
        }
      ],
      "secrets": [
        {
          "name": "ConnectionStrings__DefaultConnection",
          "valueFrom": "arn:aws:secretsmanager:${region}:${accountId}:secret:chatr/db-connection"
        },
        {
          "name": "Jwt__Key",
          "valueFrom": "arn:aws:secretsmanager:${region}:${accountId}:secret:chatr/jwt-key"
        }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "/ecs/chatr-api",
          "awslogs-region": "${region}",
          "awslogs-stream-prefix": "ecs",
          "awslogs-create-group": "true"
        }
      }
    }
  ]
}
"@

[System.IO.File]::WriteAllLines("$PWD\task-definition-ecs.json", $taskDef, $Utf8NoBomEncoding)
Write-Host "✓ task-definition-ecs.json created" -ForegroundColor Green

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "Setup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

$config = @"
AWS_ACCOUNT_ID=$accountId
AWS_REGION=$region
ECR_URI=$ecrUri
ECS_CLUSTER=chatr-cluster
ECS_SERVICE=chatr-service
"@

$config | Out-File -Encoding utf8 aws-config.txt
Write-Host "`n✓ Configuration saved to aws-config.txt" -ForegroundColor Green
'@

$script | Out-File -Encoding UTF8 complete-aws-setup.ps1
Write-Host "✓ File created successfully!" -ForegroundColor Green