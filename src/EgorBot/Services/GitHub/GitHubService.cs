using Octokit;

namespace EgorBot.Services.GitHub;

/// <summary>
/// Wraps Octokit operations for interacting with GitHub (posting comments, creating gists, etc.).
/// </summary>
public class GitHubService
{
    private readonly GitHubClient _client;
    private readonly ILogger<GitHubService> _logger;
    private readonly string _botLogin;

    public GitHubService(IConfiguration config, ILogger<GitHubService> logger)
    {
        _logger = logger;
        _botLogin = config["GitHub:BotLogin"] ?? "EgorBot";

        var token = config["GitHub:Token"] ?? "";
        _client = new GitHubClient(new ProductHeaderValue("EgorBot"))
        {
            Credentials = string.IsNullOrEmpty(token)
                ? Credentials.Anonymous
                : new Credentials(token)
        };
    }

    public string BotLogin => _botLogin;

    /// <summary>
    /// Gets recent issue/PR comments for a repository since a given timestamp.
    /// </summary>
    public async Task<IReadOnlyList<IssueComment>> GetRecentCommentsAsync(
        string owner, string repo, DateTimeOffset since, CancellationToken ct = default)
    {
        var request = new IssueCommentRequest { Since = since, Sort = IssueCommentSort.Created };
        try
        {
            return await _client.Issue.Comment.GetAllForRepository(owner, repo, request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch comments from {Owner}/{Repo}", owner, repo);
            return [];
        }
    }

    /// <summary>
    /// Posts a comment on an issue/PR.
    /// </summary>
    public async Task PostCommentAsync(string owner, string repo, int number, string body, CancellationToken ct = default)
    {
        try
        {
            await _client.Issue.Comment.Create(owner, repo, number, body);
            _logger.LogInformation("Posted comment on {Owner}/{Repo}#{Number}", owner, repo, number);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post comment on {Owner}/{Repo}#{Number}", owner, repo, number);
        }
    }

    /// <summary>
    /// Adds a reaction (e.g. thumbs-up) to a comment to acknowledge receipt.
    /// </summary>
    public async Task AddReactionAsync(string owner, string repo, long commentId, ReactionType reaction, CancellationToken ct = default)
    {
        try
        {
            await _client.Reaction.IssueComment.Create(owner, repo, (int)commentId,
                new NewReaction(reaction));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add reaction to comment {CommentId}", commentId);
        }
    }

    /// <summary>
    /// Creates a GitHub Gist with the benchmark code and returns the raw URL.
    /// </summary>
    public async Task<string> CreateBenchmarkGistAsync(string code, string jobId, CancellationToken ct = default)
    {
        try
        {
            var gist = await _client.Gist.Create(new NewGist
            {
                Description = $"EgorBot benchmark snippet (Job {jobId})",
                Public = true,
                Files = { ["Program.cs"] = code }
            });
            // Return the raw URL of the first file
            return gist.Files.Values.First().RawUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create gist for job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Checks if a number refers to a pull request (vs an issue).
    /// </summary>
    public async Task<bool> IsPullRequestAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        try
        {
            await _client.PullRequest.Get(owner, repo, number);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }
}
