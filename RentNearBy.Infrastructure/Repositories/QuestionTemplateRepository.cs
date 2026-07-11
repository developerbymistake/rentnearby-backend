using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class QuestionTemplateRepository : Repository<QuestionTemplate>, IQuestionTemplateRepository
{
    public QuestionTemplateRepository(ApplicationDbContext context) : base(context) { }
}
