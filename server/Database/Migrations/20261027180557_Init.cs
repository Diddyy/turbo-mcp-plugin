using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboMcp.Database.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase().Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "mcp_calls",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        token_id = table.Column<int>(type: "int", nullable: false),
                        player_id = table.Column<int>(type: "int", nullable: false),
                        tool = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        arguments = table
                            .Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        status = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        duration_ms = table.Column<int>(type: "int", nullable: false),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_mcp_calls", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "mcp_tokens",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        player_id = table.Column<int>(type: "int", nullable: false),
                        label = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        hash = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        prefix = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        read_only = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        last_used_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        revoked_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        revoked_by = table.Column<int>(type: "int", nullable: true),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_mcp_tokens", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_calls_created_at",
                table: "mcp_calls",
                column: "created_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_mcp_calls_token_id",
                table: "mcp_calls",
                column: "token_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tokens_hash",
                table: "mcp_tokens",
                column: "hash",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tokens_player_id",
                table: "mcp_tokens",
                column: "player_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "mcp_calls");

            migrationBuilder.DropTable(name: "mcp_tokens");
        }
    }
}
