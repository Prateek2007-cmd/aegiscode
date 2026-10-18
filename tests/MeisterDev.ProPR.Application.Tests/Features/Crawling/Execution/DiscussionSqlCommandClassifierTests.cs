// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class DiscussionSqlCommandClassifierTests
{
    [Theory]
    [InlineData("SELECT updated_at FROM findings", false)]
    [InlineData("SELECT * FROM findings FOR UPDATE", false)]
    [InlineData("-- UPDATE metadata\n SELECT 'UPDATE findings SET x=1' AS text", false)]
    [InlineData("WITH chosen AS (SELECT updated_at FROM findings) SELECT * FROM chosen", false)]
    [InlineData("UPDATE findings SET updated_at=now()", true)]
    [InlineData("INSERT INTO findings(id) VALUES (1) RETURNING id", true)]
    [InlineData("WITH chosen AS (SELECT id FROM findings) UPDATE findings SET id=2", true)]
    [InlineData("WITH source AS (SELECT * FROM x FOR UPDATE) SELECT * FROM source FOR UPDATE", false)]
    [InlineData("WITH removed AS (DELETE FROM x RETURNING id) SELECT * FROM removed", true)]
    [InlineData("SELECT '; UPDATE findings'; DELETE FROM findings", true)]
    [InlineData("SELECT $$'$$; UPDATE retained_threads SET id = id", true)]
    [InlineData("SELECT $body$'UPDATE ()$body$; DELETE FROM retained_threads", true)]
    [InlineData("/* outer /* inner */ SELECT */ UPDATE retained_threads SET id = id", true)]
    [InlineData("WITH source AS (SELECT * FROM retained_threads FOR NO KEY UPDATE) SELECT * FROM source", false)]
    [InlineData("WITH source AS (SELECT * FROM retained_threads FOR NO KEY UPDATE) UPDATE retained_threads SET id = id", true)]
    [InlineData("WITH source AS (SELECT * FROM retained_threads FOR /* lock */ NO KEY UPDATE) SELECT * FROM source", false)]
    [InlineData("SELECT E'escaped \\\' UPDATE'; DELETE FROM retained_threads", true)]
    [InlineData("SELECT '((('; /* ) */ UPDATE retained_threads SET id = id", true)]
    public void DatabaseOperationsAreClassifiedByStatementKind(string sql, bool write)
    {
        Assert.Equal(write, DiscussionSqlCommandClassifier.IsWrite(sql));
    }
}
