using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaxiApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCardholderNameToPaymentCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CardholderName",
                table: "PaymentCards",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CardholderName",
                table: "PaymentCards");
        }
    }
}
