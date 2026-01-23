$ErrorActionPreference = "Continue" # Changed to Continue to see all errors

Write-Host "Creating ECS Task Execution Role..." -ForegroundColor Green

# Get Account ID
$accountId = aws sts get-caller-identity --query Account --output text
Write-Host "AWS Account ID: $accountId" -ForegroundColor Cyan

# Check if role already exists
$roleExists = aws iam get-role --role-name ecsTaskExecutionRole 2>$null

if ($roleExists) {
    Write-Host "✓ ecsTaskExecutionRole already exists" -ForegroundColor Yellow
    Write-Host "Skipping role creation. Proceeding to attach policies..." -ForegroundColor Yellow
} else {
    # Create trust policy file
    $trustPolicy = @"
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Service": "ecs-tasks.amazonaws.com"
      },
      "Action": "sts:AssumeRole"
    }
  ]
}
"@
    $Utf8NoBomEncoding = New-Object System.Text.UTF8Encoding $False
    [System.IO.File]::WriteAllLines("$PWD\ecs-trust-policy.json", $trustPolicy, $Utf8NoBomEncoding)
    
    Write-Host "Creating role with trust policy..." -ForegroundColor Yellow
    aws iam create-role `
        --role-name ecsTaskExecutionRole `
        --assume-role-policy-document file://ecs-trust-policy.json
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ Role created successfully" -ForegroundColor Green
    } else {
        Write-Host "✗ Failed to create role" -ForegroundColor Red
        exit 1
    }
}

# Attach AWS managed policy
Write-Host "`nAttaching AWS managed policy..." -ForegroundColor Yellow
aws iam attach-role-policy `
    --role-name ecsTaskExecutionRole `
    --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy 2>$null

if ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq 255) {
    Write-Host "✓ AWS managed policy attached" -ForegroundColor Green
}

# Create CloudWatch Logs Policy file
Write-Host "`nCreating CloudWatch Logs policy..." -ForegroundColor Yellow
$cloudwatchPolicy = @"
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "logs:CreateLogGroup",
        "logs:CreateLogStream",
        "logs:PutLogEvents",
        "logs:DescribeLogStreams"
      ],
      "Resource": "arn:aws:logs:eu-west-2:*:log-group:/ecs/chatr-api:*"
    }
  ]
}
"@
$Utf8NoBomEncoding = New-Object System.Text.UTF8Encoding $False
[System.IO.File]::WriteAllLines("$PWD\cloudwatch-logs-policy.json", $cloudwatchPolicy, $Utf8NoBomEncoding)

aws iam put-role-policy `
    --role-name ecsTaskExecutionRole `
    --policy-name CloudWatchLogsPolicy `
    --policy-document file://cloudwatch-logs-policy.json

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ CloudWatch Logs policy applied" -ForegroundColor Green
} else {
    Write-Host "✗ Failed to apply CloudWatch Logs policy" -ForegroundColor Red
    Get-Content cloudwatch-logs-policy.json | Write-Host
}

# Create Secrets Manager Policy file
Write-Host "`nCreating Secrets Manager policy..." -ForegroundColor Yellow
$secretsPolicy = @"
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "secretsmanager:GetSecretValue"
      ],
      "Resource": [
        "arn:aws:secretsmanager:eu-west-2:$accountId:secret:chatr/*"
      ]
    }
  ]
}
"@
[System.IO.File]::WriteAllLines("$PWD\secrets-manager-policy.json", $secretsPolicy, $Utf8NoBomEncoding)

aws iam put-role-policy `
    --role-name ecsTaskExecutionRole `
    --policy-name SecretsManagerPolicy `
    --policy-document file://secrets-manager-policy.json

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ Secrets Manager policy applied" -ForegroundColor Green
} else {
    Write-Host "✗ Failed to apply Secrets Manager policy" -ForegroundColor Red
}

# Create ECR policy file
Write-Host "`nCreating ECR policy..." -ForegroundColor Yellow
$ecrPolicy = @"
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "ecr:GetAuthorizationToken",
        "ecr:BatchCheckLayerAvailability",
        "ecr:GetDownloadUrlForLayer",
        "ecr:BatchGetImage"
      ],
      "Resource": "*"
    }
  ]
}
"@
[System.IO.File]::WriteAllLines("$PWD\ecr-policy.json", $ecrPolicy, $Utf8NoBomEncoding)

aws iam put-role-policy `
    --role-name ecsTaskExecutionRole `
    --policy-name ECRAccessPolicy `
    --policy-document file://ecr-policy.json

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ ECR policy applied" -ForegroundColor Green
} else {
    Write-Host "✗ Failed to apply ECR policy" -ForegroundColor Red
}

# Create CloudWatch Log Group
Write-Host "`nCreating CloudWatch log group..." -ForegroundColor Yellow
$createLogGroup = aws logs create-log-group --log-group-name /ecs/chatr-api --region eu-west-2 2>&1

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ CloudWatch log group created" -ForegroundColor Green
} else {
    if ($createLogGroup -like "*ResourceAlreadyExistsException*") {
        Write-Host "✓ CloudWatch log group already exists" -ForegroundColor Green
    } else {
        Write-Host "! CloudWatch log group status unknown" -ForegroundColor Yellow
    }
}

aws logs put-retention-policy `
    --log-group-name /ecs/chatr-api `
    --retention-in-days 7 `
    --region eu-west-2 2>$null

Write-Host "✓ Retention policy set to 7 days" -ForegroundColor Green

# Create proper task definition file
Write-Host "`nCreating ECS task definition..." -ForegroundColor Yellow
$taskDef = @"
{
  "family": "chatr-task",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "256",
  "memory": "512",
  "executionRoleArn": "arn:aws:iam::$accountId:role/ecsTaskExecutionRole",
  "containerDefinitions": [
    {
      "name": "chatr-api",
      "image": "$accountId.dkr.ecr.eu-west-2.amazonaws.com/chatr-backend:latest",
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
          "valueFrom": "arn:aws:secretsmanager:eu-west-2:$accountId:secret:chatr/db-connection"
        },
        {
          "name": "Jwt__Key",
          "valueFrom": "arn:aws:secretsmanager:eu-west-2:$accountId:secret:chatr/jwt-key"
        }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "/ecs/chatr-api",
          "awslogs-region": "eu-west-2",
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

# Verify role
Write-Host "`nVerifying role..." -ForegroundColor Yellow
$role = aws iam get-role --role-name ecsTaskExecutionRole --query 'Role.Arn' --output text

if ($role) {
    Write-Host "✓ Role ARN: $role" -ForegroundColor Green
}

# Clean up temporary policy files
Write-Host "`nCleaning up temporary files..." -ForegroundColor Yellow
Remove-Item ecs-trust-policy.json -ErrorAction SilentlyContinue
Write-Host "✓ Cleanup complete" -ForegroundColor Green

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "Setup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "`nRole ARN:" -ForegroundColor Cyan
Write-Host "  $role"
Write-Host "`nAccount ID:" -ForegroundColor Cyan
Write-Host "  $accountId"
Write-Host "`nFiles created:" -ForegroundColor Cyan
Write-Host "  • cloudwatch-logs-policy.json"
Write-Host "  • secrets-manager-policy.json"
Write-Host "  • ecr-policy.json"
Write-Host "  • task-definition-ecs.json (use this for ECS deployment)"
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "  1. Create secrets in AWS Secrets Manager:"
Write-Host "     aws secretsmanager create-secret --name chatr/db-connection --secret-string `"Host=your-rds-endpoint;Port=5432;Database=ChatRDb;Username=postgres;Password=YourPassword`" --region eu-west-2"
Write-Host "     aws secretsmanager create-secret --name chatr/jwt-key --secret-string `"67&^%^%`$986070#%#HG979087078097kgfre43ikmhghdyrerfkgh(&^^**FOY^^`" --region eu-west-2"
Write-Host "  2. Create ECR repository:"
Write-Host "     aws ecr create-repository --repository-name chatr-backend --region eu-west-2"
Write-Host "  3. Create ECS cluster:"
Write-Host "     aws ecs create-cluster --cluster-name chatr-cluster --region eu-west-2"
Write-Host "  4. Create RDS PostgreSQL database (if not exists)"
Write-Host "  5. Update .github/workflows/deploy.yml with your account ID"
Write-Host "  6. Push to GitHub to trigger deployment"

# Create database connection secret
aws secretsmanager create-secret `
    --name chatr/db-connection `
    --description "ChatR Database Connection String" `
    --secret-string "Host=your-rds-endpoint.eu-west-2.rds.amazonaws.com;Port=5432;Database=ChatRDb;Username=postgres;Password=GeeBeez@2025" `
    --region eu-west-2

# Create JWT key secret
aws secretsmanager create-secret `
    --name chatr/jwt-key `
    --description "ChatR JWT Signing Key" `
    --secret-string "67&^%^%`$986070#%#HG979087078097kgfre43ikmhghdyrerfkgh(&^^**FOY^^" `
    --region eu-west-2