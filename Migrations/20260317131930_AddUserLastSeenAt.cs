using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatR2._0.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLastSeenAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All steps are written as idempotent raw SQL because this migration
            // was partially applied across multiple crash-restart cycles and the
            // DB schema is in an unknown intermediate state.

            // 1. Create composite unique index (if not already there)
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @exists = (
                    SELECT COUNT(*) FROM information_schema.statistics
                    WHERE table_schema = @db
                      AND table_name   = 'ChatMembers'
                      AND index_name   = 'IX_ChatMembers_ChatId_UserId'
                );
                SET @sql = IF(@exists = 0,
                    'CREATE UNIQUE INDEX IX_ChatMembers_ChatId_UserId ON ChatMembers (ChatId, UserId)',
                    'SELECT 1');
                PREPARE stmt FROM @sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            ");

            // 2. Drop old single-column index (only if still present)
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @exists = (
                    SELECT COUNT(*) FROM information_schema.statistics
                    WHERE table_schema = @db
                      AND table_name   = 'ChatMembers'
                      AND index_name   = 'IX_ChatMembers_ChatId'
                );
                SET @sql = IF(@exists > 0,
                    'DROP INDEX IX_ChatMembers_ChatId ON ChatMembers',
                    'SELECT 1');
                PREPARE stmt FROM @sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            ");

            // 3. Add LastSeenAt to Users (if missing)
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @exists = (
                    SELECT COUNT(*) FROM information_schema.columns
                    WHERE table_schema = @db
                      AND table_name   = 'Users'
                      AND column_name  = 'LastSeenAt'
                );
                SET @sql = IF(@exists = 0,
                    'ALTER TABLE Users ADD COLUMN LastSeenAt datetime NULL',
                    'SELECT 1');
                PREPARE stmt FROM @sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            ");

            // 4. Add PrivateKey to Chats (if missing)
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @exists = (
                    SELECT COUNT(*) FROM information_schema.columns
                    WHERE table_schema = @db
                      AND table_name   = 'Chats'
                      AND column_name  = 'PrivateKey'
                );
                SET @sql = IF(@exists = 0,
                    'ALTER TABLE Chats ADD COLUMN PrivateKey longtext CHARACTER SET utf8mb4 NULL',
                    'SELECT 1');
                PREPARE stmt FROM @sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            ");

            // 5. Add ChatId to Attachments (if missing)
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @exists = (
                    SELECT COUNT(*) FROM information_schema.columns
                    WHERE table_schema = @db
                      AND table_name   = 'Attachments'
                      AND column_name  = 'ChatId'
                );
                SET @sql = IF(@exists = 0,
                    'ALTER TABLE Attachments ADD COLUMN ChatId char(36) CHARACTER SET ascii NOT NULL DEFAULT (UUID())',
                    'SELECT 1');
                PREPARE stmt FROM @sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            ");

            // 6. Normalise datetime precision (datetime(6) → datetime) – safe to re-run
            migrationBuilder.Sql("ALTER TABLE Users    MODIFY CreatedAt  datetime NOT NULL");
            migrationBuilder.Sql("ALTER TABLE Messages MODIFY CreatedAt  datetime NOT NULL");
            migrationBuilder.Sql("ALTER TABLE Chats    MODIFY CreatedAt  datetime NOT NULL");
            migrationBuilder.Sql("ALTER TABLE ChatMembers MODIFY JoinedAt   datetime NOT NULL");
            migrationBuilder.Sql("ALTER TABLE ChatMembers MODIFY LastReadAt datetime NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMembers_ChatId_UserId",
                table: "ChatMembers");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrivateKey",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "Attachments");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Users",
                type: "datetime(6)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Messages",
                type: "datetime(6)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Chats",
                type: "datetime(6)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastReadAt",
                table: "ChatMembers",
                type: "datetime(6)",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "JoinedAt",
                table: "ChatMembers",
                type: "datetime(6)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMembers_ChatId",
                table: "ChatMembers",
                column: "ChatId");
        }
    }
}
