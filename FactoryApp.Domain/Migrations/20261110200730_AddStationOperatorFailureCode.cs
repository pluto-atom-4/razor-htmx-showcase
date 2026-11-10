using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryApp.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddStationOperatorFailureCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Operator",
                schema: "dbo",
                table: "WorkflowHistory",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Station",
                schema: "dbo",
                table: "WorkflowHistory",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                schema: "dbo",
                table: "TestRuns",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Operator",
                schema: "dbo",
                table: "WorkflowHistory");

            migrationBuilder.DropColumn(
                name: "Station",
                schema: "dbo",
                table: "WorkflowHistory");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                schema: "dbo",
                table: "TestRuns");
        }
    }
}
