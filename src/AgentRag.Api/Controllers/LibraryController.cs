using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Controllers;

public record FolderDto(int Id, string Name, int? ParentId, string Path, string Role, int? ShareRootId,
    string? ShareRootName, bool CanWrite, bool CanEdit, bool CanManage, int DocumentCount, int FolderCount);
public record FolderRequest(string Name, int? ParentId = null);
public record MoveRequest(int? FolderId, bool ConfirmAccessChange = false);
public record RenameRequest(string Name);
public record MemberRequest(string Email, string Role);
public record MemberRoleRequest(string Role);
public record MemberDto(string UserId, string Email, string Role);
/// <param name="Repositories">Connected repositories whose folder is visible; lets the library show synchronisation state.</param>
public record LibraryDto(IReadOnlyList<FolderDto> Folders, IReadOnlyList<DocumentDto> Documents, string Revision,
    IReadOnlyList<RepositoryDto>? Repositories = null);

[ApiController, Authorize, Route("api/library")]
public class LibraryController(AppDbContext db) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private LibraryAccess Access => new(db);

    [HttpGet]
    public async Task<LibraryDto> Get(CancellationToken ct)
    {
        var access = await Access.LoadAsync(UserId, ct);
        var docs = await access.Documents(db).OrderBy(d => d.Id)
            .Select(d => new { Document = d, Chunks = d.Chunks.Count,
                AddedBy = db.Users.Where(u => u.Id == d.AddedById).Select(u => u.DisplayName ?? "Contributor").FirstOrDefault() }).ToListAsync(ct);
        var documents = docs.Select(d => DocumentDto.From(d.Document, d.Chunks, access.CanWriteDocument(d.Document), d.AddedBy)).ToArray();
        var visible = access.Folders.Values.Where(f => access.Role(f.Id) is not null).OrderBy(f => f.Name).ThenBy(f => f.Id);
        var folders = visible.Select(f =>
        {
            var root = access.ShareRoot(f.Id);
            var descendants = access.Descendants(f.Id);
            return new FolderDto(f.Id, f.Name, f.ParentId is { } p && access.Role(p) is not null ? p : null,
                access.Path(f.Id), access.Role(f.Id)!, root, root is { } r ? access.Folders[r].Name : null,
                access.CanWrite(f.Id), access.CanEditFolder(f.Id), f.OwnerId == UserId,
                documents.Count(d => d.FolderId is { } id && descendants.Contains(id)), descendants.Count - 1);
        }).ToArray();
        // A change to the accessible corpus/permissions invalidates client-held answers and passages.
        var revision = await Access.RevisionAsync(access, ct);
        Response.Headers.CacheControl = "no-store";
        return new(folders, documents, revision, await RepositoriesController.ListAsync(db, UserId, ct));
    }

    [HttpPost("folders")]
    public async Task<IActionResult> Create(FolderRequest request, CancellationToken ct)
    {
        var name = Name(request.Name);
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await Access.LoadAsync(UserId, ct);
        var owner = request.ParentId is { } id ? access.Require(id, write: true).OwnerId : UserId;
        var folder = new KnowledgeFolder { Name = name, OwnerId = owner, ParentId = request.ParentId };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Ok(new { folder.Id });
    }

    [HttpPatch("folders/{id:int}")]
    public async Task<IActionResult> Rename(int id, RenameRequest request, CancellationToken ct)
    {
        var name = Name(request.Name);
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await Access.LoadAsync(UserId, ct);
        access.Require(id, edit: true);
        var folder = await db.Folders.SingleAsync(f => f.Id == id, ct);
        folder.Name = name;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("folders/{id:int}/move")]
    public async Task<IActionResult> Move(int id, MoveRequest request, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await Access.LoadAsync(UserId, ct);
        var source = access.Require(id, edit: true);
        var descendants = access.Descendants(id);
        if (request.FolderId is { } targetId)
        {
            var target = access.Require(targetId, write: true);
            if (target.OwnerId != source.OwnerId) throw new LibraryException(400, "Moving between different owners is not supported.");
            if (descendants.Contains(targetId)) throw new LibraryException(400, "A folder cannot be moved into itself or its descendants.");
        }
        else if (source.OwnerId != UserId) throw new LibraryException(403, "Only the owner can move content outside a shared folder.");
        var sourceRoot = access.ShareRoot(id);
        var targetRoot = request.FolderId is { } targetFolder ? access.ShareRoot(targetFolder) : null;
        // A directly shared subtree may move within private space, keeping its existing grants.
        var resultingRoot = sourceRoot == id ? id : targetRoot;
        if (targetRoot is not null && access.Members.Any(m => descendants.Contains(m.FolderId)))
            throw new LibraryException(409, "Stop sharing the nested folders before moving them into a shared folder.");
        CheckAccessChange(source.OwnerId, sourceRoot, resultingRoot, request.ConfirmAccessChange);
        var folder = await db.Folders.SingleAsync(f => f.Id == id, ct);
        folder.ParentId = request.FolderId;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("folders/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await Access.LoadAsync(UserId, ct);
        access.Require(id, edit: true);
        var ids = access.Descendants(id);
        // A writer cannot delete an ancestor containing an independently shared boundary.
        if (access.Role(id) != "owner" && access.Members.Any(m => ids.Contains(m.FolderId)))
            throw new LibraryException(403, "Only the owner can delete a shared folder.");
        var documents = await db.Documents.Where(d => d.FolderId != null && ids.Contains(d.FolderId.Value)).ToListAsync(ct);
        db.Documents.RemoveRange(documents);
        db.FolderMembers.RemoveRange(await db.FolderMembers.Where(m => ids.Contains(m.FolderId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        // Restrict parent deletion; remove leaves first rather than relying on recursive cascades.
        while (ids.Count > 0)
        {
            var leaves = ids.Where(key => !access.Folders.Values.Any(f => f.ParentId == key && ids.Contains(f.Id))).ToArray();
            if (leaves.Length == 0) throw new LibraryException(409, "The folder hierarchy contains a cycle.");
            db.Folders.RemoveRange(await db.Folders.Where(f => leaves.Contains(f.Id)).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            ids.ExceptWith(leaves);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("folders/{id:int}/members")]
    public async Task<IReadOnlyList<MemberDto>> Members(int id, CancellationToken ct)
    {
        var access = await Access.LoadAsync(UserId, ct);
        RequireShareOwner(access, id);
        return await db.FolderMembers.Where(m => m.FolderId == id).OrderBy(m => m.User.Email)
            .Select(m => new MemberDto(m.UserId, m.User.Email!, m.Role)).ToListAsync(ct);
    }

    [HttpPost("folders/{id:int}/members")]
    public async Task<IActionResult> AddMember(int id, MemberRequest request, CancellationToken ct)
    {
        ValidateRole(request.Role);
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await Access.LoadAsync(UserId, ct);
        RequireShareOwner(access, id);
        var descendants = access.Descendants(id);
        if (access.Members.Any(m => m.FolderId != id && descendants.Contains(m.FolderId)))
            throw new LibraryException(409, "Stop sharing the nested folders before sharing their parent.");
        var email = request.Email?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 256) throw new LibraryException(400, "Enter a registered user's email address.");
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == email, ct);
        if (user is null) throw new LibraryException(400, "No registered account matches this email address.");
        if (user.Id == UserId) throw new LibraryException(400, "The owner already has full access.");
        var member = await db.FolderMembers.FindAsync([id, user.Id], ct);
        if (member is not null) throw new LibraryException(409, "This person already has access. Change their role below.");
        db.FolderMembers.Add(new FolderMember { FolderId = id, UserId = user.Id, Role = request.Role });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPatch("folders/{id:int}/members/{userId}")]
    public async Task<IActionResult> ChangeMember(int id, string userId, MemberRoleRequest request, CancellationToken ct)
    {
        ValidateRole(request.Role);
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        RequireShareOwner(await Access.LoadAsync(UserId, ct), id);
        var member = await db.FolderMembers.FindAsync([id, userId], ct) ?? throw new LibraryException(404, "Member not found.");
        member.Role = request.Role;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("folders/{id:int}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(int id, string userId, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        RequireShareOwner(await Access.LoadAsync(UserId, ct), id);
        var member = await db.FolderMembers.FindAsync([id, userId], ct);
        if (member is null) return NotFound();
        db.FolderMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("folders/{id:int}/sharing")]
    public async Task<IActionResult> StopSharing(int id, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        RequireShareOwner(await Access.LoadAsync(UserId, ct), id);
        db.FolderMembers.RemoveRange(await db.FolderMembers.Where(m => m.FolderId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("folders/{id:int}/membership")]
    public async Task<IActionResult> Leave(int id, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await Access.LoadAsync(UserId, ct);
        access.Require(id);
        if (access.Role(id) == "owner" || access.ShareRoot(id) != id)
            throw new LibraryException(400, "Leave the shared root folder instead.");
        var member = await db.FolderMembers.FindAsync([id, UserId], ct);
        if (member is null) return NotFound();
        db.FolderMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    private static void RequireShareOwner(LibrarySnapshot access, int id)
    {
        access.Require(id, owner: true);
        if (access.ShareRoot(id) is { } root && root != id)
            throw new LibraryException(409, "Access is inherited. Manage sharing on the shared parent folder.");
    }

    private void CheckAccessChange(string owner, int? from, int? to, bool confirmed)
    {
        if (from == to) return;
        if (owner != UserId) throw new LibraryException(403, "Only the owner can change the sharing perimeter.");
        if (!confirmed) throw new LibraryException(409, "Confirm the change to who can access this content.");
    }

    internal static string Name(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 256) throw new LibraryException(400, "Enter a name of 1 to 256 characters.");
        return name;
    }

    private static void ValidateRole(string role)
    {
        if (role is not ("read" or "write")) throw new LibraryException(400, "Choose read or write access.");
    }
}
