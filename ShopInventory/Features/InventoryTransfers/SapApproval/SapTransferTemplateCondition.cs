using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Features.InventoryTransfers.SapApproval;

/// <summary>
/// The condition of one SAP approval template on inventory transfers, read from its user query, and
/// whether it holds for a transfer between two warehouses.
/// </summary>
/// <remarks>
/// <para>Every transfer template in SAP is a user query of one shape, measured on the test company
/// (its 24 active transfer templates, 2026-11-12):</para>
/// <code>
/// SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0."ToWhsCode" = $[OWTR.ToWhsCode] and T0."ToWhsCode" = 'CMB'
/// SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0."Filler" = $[OWTR.Filler]and T0."Filler" = 'CMB'
///     and T0."ToWhsCode"= $[OWTR.ToWhsCode] and T0."ToWhsCode" NOT Like'CMFS' and …
/// SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0."Filler" = 'CMTRANS'and (T0."ToWhsCode" = 'DISPCH' OR …)
/// </code>
/// <para><c>Filler</c> is the transfer's source warehouse and <c>ToWhsCode</c> its destination;
/// <c>$[OWTR.x]</c> is the value on the document being added. The B1 client runs the query and holds
/// the transfer when it returns a row. SAP cannot run it for a document that arrives over the Service
/// Layer, so this reads the WHERE clause and answers it for the warehouses the transfer will carry.</para>
///
/// <para>Strictly, <c>T0</c> is any transfer already in SAP, and a clause like <c>T0."ToWhsCode" =
/// $[OWTR.ToWhsCode]</c> ties it to the document. Here <c>T0</c> is read as the document itself. The
/// two agree whenever SAP has ever held a transfer on the same route — true of every route a request
/// is raised for — and the reading keeps an unsatisfiable condition, such as template #66's
/// <c>"ToWhsCode" = 'DISPICE' and "ToWhsCode" = 'EXPHW'</c>, from matching anything, as it does in SAP.</para>
///
/// <para>Anything outside that shape — another column, another table, a function — cannot be answered
/// before the transfer exists, and the condition says so in <see cref="Unreadable"/> rather than
/// guessing.</para>
/// </remarks>
public sealed partial class SapTransferTemplateCondition
{
    private readonly Node? _root;

    private SapTransferTemplateCondition(Node? root, string? unreadable)
    {
        _root = root;
        Unreadable = unreadable;
    }

    /// <summary>Why the query cannot be answered here; null when it can.</summary>
    public string? Unreadable { get; }

    public bool IsReadable => Unreadable is null;

    /// <summary>
    /// Whether SAP would hold a transfer from <paramref name="fromWarehouse"/> to
    /// <paramref name="toWarehouse"/> under this condition. False for an unreadable condition.
    /// </summary>
    public bool Matches(string? fromWarehouse, string? toWarehouse)
        => _root is not null && _root.Evaluate(new Route(fromWarehouse?.Trim() ?? string.Empty, toWarehouse?.Trim() ?? string.Empty));

    public static SapTransferTemplateCondition Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new(null, "the template's query is empty");

        var text = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(';').Trim();
        var shape = QueryShape().Match(text);
        if (!shape.Success)
            return new(null, "the query is not a SELECT 'TRUE' over OWTR (or DUMMY) with a WHERE clause");

        var alias = shape.Groups["alias"].Success ? shape.Groups["alias"].Value : null;
        if (alias is not null && Keywords.Contains(alias))
            alias = null;

        try
        {
            var tokens = Tokenize(shape.Groups["where"].Value);
            var parser = new Parser(tokens, alias);
            var root = parser.ParseExpression();
            parser.ExpectEnd();
            return new(root, null);
        }
        catch (UnreadableQueryException exception)
        {
            return new(null, exception.Message);
        }
    }

    // ── Tokens ─────────────────────────────────────────

    private enum TokenKind { Identifier, Literal, DocumentField, Operator, OpenParen, CloseParen, Comma }

    private sealed record Token(TokenKind Kind, string Text);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "LIKE", "IN", "WHERE"
    };

    private static List<Token> Tokenize(string where)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < where.Length)
        {
            var c = where[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '$' && i + 1 < where.Length && where[i + 1] == '[')
            {
                var close = where.IndexOf(']', i);
                if (close < 0)
                    throw new UnreadableQueryException("a $[…] document reference is not closed");
                var reference = DocumentReference().Match(where[(i + 2)..close].Trim());
                if (!reference.Success)
                    throw new UnreadableQueryException($"it reads $[{where[(i + 2)..close]}], which is not a transfer header field");
                tokens.Add(new(TokenKind.DocumentField, reference.Groups["field"].Value));
                i = close + 1;
                continue;
            }

            if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < where.Length && where[i + 1] == '\''))
            {
                if (c != '\'') i++;
                var value = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= where.Length)
                        throw new UnreadableQueryException("a quoted value is not closed");
                    if (where[i] == '\'')
                    {
                        if (i + 1 < where.Length && where[i + 1] == '\'')
                        {
                            value.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(where[i++]);
                }

                tokens.Add(new(TokenKind.Literal, value.ToString()));
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new(TokenKind.OpenParen, "("));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new(TokenKind.CloseParen, ")"));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new(TokenKind.Comma, ","));
                    i++;
                    continue;
                case '=':
                    tokens.Add(new(TokenKind.Operator, "="));
                    i++;
                    continue;
                case '<' when i + 1 < where.Length && where[i + 1] == '>':
                case '!' when i + 1 < where.Length && where[i + 1] == '=':
                    tokens.Add(new(TokenKind.Operator, "<>"));
                    i += 2;
                    continue;
            }

            if (c == '"' || char.IsLetter(c) || c == '_')
            {
                // An identifier, possibly qualified: T0."ToWhsCode", T0.Filler, "Filler", AND.
                var parts = new List<string>();
                while (true)
                {
                    if (i < where.Length && where[i] == '"')
                    {
                        var close = where.IndexOf('"', i + 1);
                        if (close < 0)
                            throw new UnreadableQueryException("a quoted column name is not closed");
                        parts.Add(where[(i + 1)..close]);
                        i = close + 1;
                    }
                    else
                    {
                        var start = i;
                        while (i < where.Length && (char.IsLetterOrDigit(where[i]) || where[i] == '_'))
                            i++;
                        if (start == i)
                            throw new UnreadableQueryException($"it holds '{where[start..]}', which is not a column");
                        parts.Add(where[start..i]);
                    }

                    if (i < where.Length && where[i] == '.')
                    {
                        i++;
                        continue;
                    }

                    break;
                }

                tokens.Add(new(TokenKind.Identifier, string.Join('.', parts)));
                continue;
            }

            throw new UnreadableQueryException($"it holds '{c}', which this reader does not understand");
        }

        return tokens;
    }

    // ── Parser ─────────────────────────────────────────

    private sealed class Parser(List<Token> tokens, string? alias)
    {
        private int _position;

        public Node ParseExpression()
        {
            var left = ParseAnd();
            while (TakeKeyword("OR"))
                left = new Or(left, ParseAnd());
            return left;
        }

        public void ExpectEnd()
        {
            if (_position < tokens.Count)
                throw new UnreadableQueryException($"it continues with '{tokens[_position].Text}' where the condition should end");
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (TakeKeyword("AND"))
                left = new And(left, ParseNot());
            return left;
        }

        private Node ParseNot()
        {
            if (TakeKeyword("NOT"))
                return new Not(ParseNot());
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            if (Peek() is { Kind: TokenKind.OpenParen })
            {
                _position++;
                var inner = ParseExpression();
                Expect(TokenKind.CloseParen, ")");
                return inner;
            }

            var left = ParseOperand();
            var negated = TakeKeyword("NOT");

            if (TakeKeyword("LIKE"))
                return Negate(new Like(left, ParseOperand()), negated);

            if (TakeKeyword("IN"))
            {
                Expect(TokenKind.OpenParen, "(");
                var values = new List<Operand> { ParseOperand() };
                while (Peek() is { Kind: TokenKind.Comma })
                {
                    _position++;
                    values.Add(ParseOperand());
                }

                Expect(TokenKind.CloseParen, ")");
                return Negate(new In(left, values), negated);
            }

            if (negated)
                throw new UnreadableQueryException("NOT is followed by neither LIKE nor IN");

            var op = Peek();
            if (op is not { Kind: TokenKind.Operator })
                throw new UnreadableQueryException(op is null
                    ? "a comparison is cut short"
                    : $"'{op.Text}' follows a value where a comparison was expected");
            _position++;
            var right = ParseOperand();
            return op.Text == "=" ? new Equal(left, right) : new Not(new Equal(left, right));
        }

        private Operand ParseOperand()
        {
            var token = Peek() ?? throw new UnreadableQueryException("a comparison is missing its value");
            _position++;
            return token.Kind switch
            {
                TokenKind.Literal => new LiteralOperand(token.Text),
                TokenKind.DocumentField => ToField(token.Text),
                TokenKind.Identifier when !Keywords.Contains(token.Text) => ColumnOperand(token.Text),
                _ => throw new UnreadableQueryException($"'{token.Text}' stands where a column or a value was expected")
            };
        }

        private Operand ColumnOperand(string identifier)
        {
            var parts = identifier.Split('.');
            if (parts.Length > 2 ||
                (parts.Length == 2 && (alias is null || !string.Equals(parts[0], alias, StringComparison.OrdinalIgnoreCase))))
                throw new UnreadableQueryException($"it tests {identifier}, which is not a column of the transfer");
            return ToField(parts[^1]);
        }

        private static Operand ToField(string column) => column.ToUpperInvariant() switch
        {
            "FILLER" => new FieldOperand(RouteEnd.From),
            "TOWHSCODE" => new FieldOperand(RouteEnd.To),
            _ => throw new UnreadableQueryException(
                $"it tests {column}, and only the source (Filler) and destination (ToWhsCode) are known before the transfer exists")
        };

        private static Node Negate(Node node, bool negated) => negated ? new Not(node) : node;

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private bool TakeKeyword(string keyword)
        {
            if (Peek() is { Kind: TokenKind.Identifier } token &&
                string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase))
            {
                _position++;
                return true;
            }

            return false;
        }

        private void Expect(TokenKind kind, string text)
        {
            if (Peek()?.Kind != kind)
                throw new UnreadableQueryException($"'{text}' was expected");
            _position++;
        }
    }

    // ── Evaluation ─────────────────────────────────────

    private enum RouteEnd { From, To }

    private readonly record struct Route(string From, string To);

    private abstract record Operand
    {
        public abstract string Value(Route route);
    }

    private sealed record LiteralOperand(string Text) : Operand
    {
        public override string Value(Route route) => Text;
    }

    private sealed record FieldOperand(RouteEnd End) : Operand
    {
        public override string Value(Route route) => End == RouteEnd.From ? route.From : route.To;
    }

    private abstract record Node
    {
        public abstract bool Evaluate(Route route);
    }

    private sealed record And(Node Left, Node Right) : Node
    {
        public override bool Evaluate(Route route) => Left.Evaluate(route) && Right.Evaluate(route);
    }

    private sealed record Or(Node Left, Node Right) : Node
    {
        public override bool Evaluate(Route route) => Left.Evaluate(route) || Right.Evaluate(route);
    }

    private sealed record Not(Node Inner) : Node
    {
        public override bool Evaluate(Route route) => !Inner.Evaluate(route);
    }

    // Warehouse codes are compared as SAP stores them, trailing blanks aside; case is ignored so a
    // template typed in lower case still reads the way its author meant.
    private sealed record Equal(Operand Left, Operand Right) : Node
    {
        public override bool Evaluate(Route route)
            => string.Equals(Left.Value(route).TrimEnd(), Right.Value(route).TrimEnd(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record In(Operand Left, List<Operand> Values) : Node
    {
        public override bool Evaluate(Route route)
            => Values.Any(value => new Equal(Left, value).Evaluate(route));
    }

    private sealed record Like(Operand Left, Operand Pattern) : Node
    {
        public override bool Evaluate(Route route)
        {
            var pattern = Pattern.Value(route);
            var regex = "^" + string.Concat(pattern.Select(c => c switch
            {
                '%' => ".*",
                '_' => ".",
                _ => Regex.Escape(c.ToString())
            })) + "$";
            return Regex.IsMatch(Left.Value(route).TrimEnd(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    private sealed class UnreadableQueryException(string message) : Exception(message);

    [GeneratedRegex(
        """^SELECT\s+(?:DISTINCT\s+)?'TRUE'\s+FROM\s+(?:"?OWTR"?|"?DUMMY"?)(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_][A-Za-z0-9_]*))?\s+WHERE\s+(?<where>.+)$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex QueryShape();

    [GeneratedRegex("""^\$?OWTR\s*\.\s*"?(?<field>[A-Za-z_][A-Za-z0-9_]*)"?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentReference();
}
