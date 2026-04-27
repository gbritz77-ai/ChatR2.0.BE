using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatR2._0.Migrations
{
    public partial class AddPasswordResetFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @e1 = (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=@db AND table_name='Users' AND column_name='PasswordResetToken');
                SET @s1 = IF(@e1=0, 'ALTER TABLE Users ADD COLUMN PasswordResetToken varchar(100) NULL', 'SELECT 1');
                PREPARE st FROM @s1; EXECUTE st; DEALLOCATE PREPARE st;
            ");
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @e2 = (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=@db AND table_name='Users' AND column_name='PasswordResetTokenExpiry');
                SET @s2 = IF(@e2=0, 'ALTER TABLE Users ADD COLUMN PasswordResetTokenExpiry datetime(6) NULL', 'SELECT 1');
                PREPARE st FROM @s2; EXECUTE st; DEALLOCATE PREPARE st;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PasswordResetToken", table: "Users");
            migrationBuilder.DropColumn(name: "PasswordResetTokenExpiry", table: "Users");
        }
    }
}
