Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "  1. Ensure the following secrets exist in AWS Secrets Manager:" -ForegroundColor Yellow
Write-Host "     - chatr/db-connection" -ForegroundColor Yellow
Write-Host "     - chatr/jwt-key" -ForegroundColor Yellow
Write-Host "  2. Create ECR repository:" -ForegroundColor Yellow
Write-Host "     aws ecr create-repository --repository-name chatr-backend --region eu-west-2" -ForegroundColor Yellow
Write-Host "  3. Create ECS cluster:" -ForegroundColor Yellow
Write-Host "     aws ecs create-cluster --cluster-name chatr-cluster --region eu-west-2" -ForegroundColor Yellow
Write-Host "  4. Create RDS PostgreSQL database (if not exists)" -ForegroundColor Yellow
Write-Host "  5. Push to GitHub to trigger deployment" -ForegroundColor Yellow

# -------------------------------
# Create / Update Secrets SAFELY
# -------------------------------
Write-Host "`nCreating/Updating Secrets Manager values (no hardcoded secrets)..." -ForegroundColor Yellow

# Prompt for MariaDB endpoint + username + password at runtime
$dbHost = Read-Host "Enter MariaDB host (e.g. 3.253.55.197)"
$dbPort = Read-Host "Enter MariaDB port (default 3306)"
if ([string]::IsNullOrWhiteSpace($dbPort)) { $dbPort = "3306" }

$dbName = Read-Host "Enter MariaDB database name"
$dbUser = Read-Host "Enter MariaDB username"
$dbPasswordSecure = Read-Host "Enter MariaDB password" -AsSecureString

# Convert SecureString -> plain (needed because AWS CLI needs a real string)
$dbPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($dbPasswordSecure)
)

# MariaDB/MySQL connection string (ADO.NET style)
$dbConnectionString = "Server=$dbHost;Port=$dbPort;Database=$dbName;User ID=$dbUser;Password=$dbPasswordPlain;SslMode=Preferred;"

# Generate strong JWT key at runtime (base64)
Add-Type -AssemblyName System.Security
$jwtBytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($jwtBytes)
$jwtKey = [Convert]::ToBase64String($jwtBytes)

function Upsert-Secret {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$Description,
        [Parameter(Mandatory=$true)][string]$SecretString
    )

    # Check if secret exists
    $exists = aws secretsmanager describe-secret --secret-id $Name --region eu-west-2 2>$null
    if ($LASTEXITCODE -eq 0) {
        aws secretsmanager update-secret `
            --secret-id $Name `
            --description $Description `
            --secret-string $SecretString `
            --region eu-west-2 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ Updated secret: $Name" -ForegroundColor Green
        } else {
            Write-Host "✗ Failed to update secret: $Name" -ForegroundColor Red
        }
    } else {
        aws secretsmanager create-secret `
            --name $Name `
            --description $Description `
            --secret-string $SecretString `
            --region eu-west-2 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ Created secret: $Name" -ForegroundColor Green
        } else {
            Write-Host "✗ Failed to create secret: $Name" -ForegroundColor Red
        }
    }
}

Upsert-Secret -Name "chatr/db-connection" -Description "ChatR Database Connection String" -SecretString $dbConnectionString
Upsert-Secret -Name "chatr/jwt-key" -Description "ChatR JWT Signing Key" -SecretString $jwtKey

# Clean sensitive variables from memory (best-effort)
$dbPasswordPlain = $null
$jwtKey = $null
[GC]::Collect()
