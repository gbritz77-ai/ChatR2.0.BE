using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatR2._0.Migrations
{
    public partial class AddUserAvailability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @e1 = (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=@db AND table_name='Users' AND column_name='AvailabilityDays');
                SET @s1 = IF(@e1=0, 'ALTER TABLE Users ADD COLUMN AvailabilityDays varchar(50) NULL', 'SELECT 1');
                PREPARE st FROM @s1; EXECUTE st; DEALLOCATE PREPARE st;
            ");
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @e2 = (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=@db AND table_name='Users' AND column_name='AvailabilityFrom');
                SET @s2 = IF(@e2=0, 'ALTER TABLE Users ADD COLUMN AvailabilityFrom varchar(10) NULL', 'SELECT 1');
                PREPARE st FROM @s2; EXECUTE st; DEALLOCATE PREPARE st;
            ");
            migrationBuilder.Sql(@"
                SET @db = DATABASE();
                SET @e3 = (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=@db AND table_name='Users' AND column_name='AvailabilityTo');
                SET @s3 = IF(@e3=0, 'ALTER TABLE Users ADD COLUMN AvailabilityTo varchar(10) NULL', 'SELECT 1');
                PREPARE st FROM @s3; EXECUTE st; DEALLOCATE PREPARE st;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AvailabilityDays", table: "Users");
            migrationBuilder.DropColumn(name: "AvailabilityFrom", table: "Users");
            migrationBuilder.DropColumn(name: "AvailabilityTo", table: "Users");
        }
    }
}
