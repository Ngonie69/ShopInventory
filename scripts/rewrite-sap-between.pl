#!/usr/bin/perl
# SAP's SQLQueries validator rejects BETWEEN when both bounds are bound parameters: it strips the
# whitespace before parsing, so `x BETWEEN :a AND :b` reaches the grammar as `xBETWEEN:aAND:b` and
# comes back as error 701, "Invalid parameterized expression". Two single-parameter comparisons are
# the shape every working report already uses, so rewrite to that.
#
# Usage: perl scripts/rewrite-sap-between.pl [--check] <file>...
#   --check  report offending sites and exit 1 without writing (for CI / re-verification)
use strict;
use warnings;

my $check = 0;
my @files;
for my $arg (@ARGV) {
    if ($arg eq '--check') { $check = 1 } else { push @files, $arg }
}

# Column expression, then BETWEEN, then two bound parameters. The expression must start at a word
# character: these constants live in C# string literals, and a leading `"` belongs to the literal,
# not to the column — capturing it duplicates the quote and breaks the source.
my $column = qr/\w[\w.]*(?:"{1,2}\w+"{1,2})?/;

# Only both-bound BETWEEN is rejected; a literal bound parses, so require the sigil on both sides.
my $pattern = qr/($column)\s+BETWEEN\s+(:\w+)\s+AND\s+(:\w+)/;

my $hits = 0;
for my $file (@files) {
    open my $in, '<', $file or die "cannot read $file: $!";
    my @lines = <$in>;
    close $in;

    my $changed = 0;
    for my $i (0 .. $#lines) {
        # A comment describing the pattern is not SQL; the guard test's own remarks quote it.
        next if $lines[$i] =~ m{^\s*//};
        next unless $lines[$i] =~ $pattern;
        $hits++;
        printf "%s:%d: %s", $file, $i + 1, $lines[$i];
        next if $check;
        $lines[$i] =~ s/$pattern/$1 >= $2 AND $1 <= $3/g;
        $changed = 1;
    }

    next unless $changed;
    open my $out, '>', $file or die "cannot write $file: $!";
    print $out @lines;
    close $out;
    print "  -> rewritten\n";
}

if ($check) {
    print $hits ? "FAIL: $hits both-bound BETWEEN site(s) SAP will reject\n" : "OK: no both-bound BETWEEN in SAP SQL\n";
    exit($hits ? 1 : 0);
}
print "rewrote $hits site(s)\n";
