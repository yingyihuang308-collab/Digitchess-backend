using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Backend;
public sealed class DesignFactory:IDesignTimeDbContextFactory<GameDb>
{
    public GameDb CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<GameDb>().UseNpgsql("Host=localhost;Database=design_only").Options);
}
