provider "aws" {
  region = "eu-west-2"
}

# ECR Repository
resource "aws_ecr_repository" "chatr_backend" {
  name                 = "chatr-backend"
  image_tag_mutability = "MUTABLE"
}

# ECS Cluster
resource "aws_ecs_cluster" "chatr" {
  name = "chatr-cluster"
}

# VPC and Networking (simplified - expand for production)
resource "aws_vpc" "main" {
  cidr_block           = "10.0.0.0/16"
  enable_dns_hostnames = true
  enable_dns_support   = true
}

resource "aws_subnet" "public_1" {
  vpc_id            = aws_vpc.main.id
  cidr_block        = "10.0.1.0/24"
  availability_zone = "us-east-1a"
}

resource "aws_subnet" "public_2" {
  vpc_id            = aws_vpc.main.id
  cidr_block        = "10.0.2.0/24"
  availability_zone = "us-east-1b"
}

# RDS PostgreSQL
resource "aws_db_instance" "postgres" {
  identifier           = "ChatRDb"
  engine               = "postgres"
  engine_version       = "15.4"
  instance_class       = "db.t3.micro"
  allocated_storage    = 20
  username             = "postgres"
  password             = var.db_password # Use secrets!
  db_name              = "ChatRDb"
  skip_final_snapshot  = true
}

# ALB
resource "aws_lb" "chatr" {
  name               = "chatr-alb"
  internal           = false
  load_balancer_type = "application"
  subnets            = [aws_subnet.public_1.id, aws_subnet.public_2.id]
}

resource "aws_lb_target_group" "chatr" {
  name        = "chatr-tg"
  port        = 8080
  protocol    = "HTTP"
  vpc_id      = aws_vpc.main.id
  target_type = "ip"

  health_check {
    path = "/health"
  }
}

# ECS Service
resource "aws_ecs_service" "chatr" {
  name            = "chatr-service"
  cluster         = aws_ecs_cluster.chatr.id
  task_definition = aws_ecs_task_definition.chatr.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  network_configuration {
    subnets = [aws_subnet.public_1.id, aws_subnet.public_2.id]
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.chatr.arn
    container_name   = "chatr-api"
    container_port   = 8080
  }
}