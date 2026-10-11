#!/usr/bin/env perl
# find-name-sites.pl — the by-name reader calls whose MEMBER NAME is not a string literal, traced to the literals
# behind them. The third extraction pass of tools/check-catalog.sh (2026-10-11).
#
# The catalog gate's first two passes read the literal INSIDE the reader call — `GetMember(x, "Name")`. A name that
# arrives through a table was invisible to both, and the gate still said "all N catalogued":
#
#     static readonly string[] RenderMatFields = { "currentRenderMaterial", "runTimeRenderMaterial" };
#     foreach (var fld in RenderMatFields) if (GetMember(ro, fld) is Material mat) …       // three sites, 0 catalogued
#     foreach (var gn in new[] { "midResMaterialGuid", "highResMaterialGuid" }) ro.GetType().GetField(gn, BF)
#     object OliGet(string p) => vt.GetProperty(p, BF)?.GetValue(vo);   OliGet("visualOutput");
#
# Every such site is resolved here. For a reader call whose name argument is an identifier (or `Table[i]`):
#   1. a loop variable — the nearest preceding `foreach (var X in …)` — resolves to its source: an inline
#      `new[] { "a", "b" }` gives its literals, an identifier is looked up as a table (2);
#   2. a table — `string[] X = { … }` / `= new[] { … }` anywhere in the runtime sources, or `const string X = "a"`,
#      or `var X = cond ? "a" : "b"` in the same file — gives its literals only when every element is literal;
#   3. a bounded `for (int i = 0; i < N; i++)` with `"Prefix" + i + ".Suffix"` enumerates every name;
#   4. a `string` PARAMETER of the enclosing method or local function: a function in --known (the gate's HELPERS and
#      NOT_MEMBER_READERS lists) forwards its caller's literal, which the first two passes already extract, so nothing
#      is printed; any OTHER function (a local function, a private static the gate does not know) is a wrapper of its
#      own, and every call to it in the same file is resolved the same way, argument by argument, two levels deep;
#   5. anything else is printed as UNRESOLVED — the gate FAILS on it unless the site is allowlisted with a reason.
# A loop is used only inside its body; a parameter shadows a table; ambiguous table declarations fail closed.
#
# Output, one per line:
#     <name>\t<file>:<line>                         a literal the site reads (the line is the READER site)
#     UNRESOLVED\t<argument>\t<file>:<line>\t<enclosing function>
#
#   perl tools/find-name-sites.pl --helpers "<names>" --known "<names>" <file.cs>...
use strict;
use warnings;

my ($helpers, $known) = ('', '');
while (@ARGV && $ARGV[0] =~ /^--/) {
    my $opt = shift @ARGV;
    if    ($opt eq '--helpers') { $helpers = shift @ARGV }
    elsif ($opt eq '--known')   { $known   = shift @ARGV }
    else { die "unknown option $opt\n" }
}
my %known = map { $_ => 1 } split ' ', "$helpers $known";
my $helperAlt = join '|', grep { length } split ' ', $helpers;
die "--helpers is required\n" unless length $helperAlt;

# the accessor shapes of tools/check-catalog.sh; which argument carries the member name is per shape
my $accessor = qr/(?:\b(?<h>$helperAlt)|(?<fm>FastMember\.(?:Getter|Setter)<[^>]*>)|(?<at>AccessTools\.(?:Field|Property|Method|PropertyGetter|PropertySetter|DeclaredField|DeclaredProperty|DeclaredMethod))|(?<gx>\.Get(?:Field|Property|Method|Event|Member))|(?<tr>Traverse\.(?:Field|Property|Method)))\s*\(/;

# Two views of each file, same length so offsets agree: CODE has comments and string CONTENTS blanked (safe for
# brace and paren matching); TEXT has only the comments blanked (the literals are read from it). Interpolated strings
# keep their holes as code in both, as tools/find-reader-helpers.pl does.
sub views {
    my ($s) = @_;
    my $n = length $s;
    my @code = split //, $s;
    my @text = split //, $s;
    my @stack;   # per open interpolated string: [verbatim, holeDepth]
    my $blank = sub { my ($arr, $a, $b) = @_; $b = $n - 1 if $b > $n - 1; for my $k ($a .. $b) { $arr->[$k] = ' ' if $arr->[$k] ne "\n" } };
    my $i = 0;
    while ($i < $n) {
        my $ch = $code[$i];
        my $nx = $i + 1 < $n ? $code[$i + 1] : '';
        if (@stack && $stack[-1][1] == 0) {                       # interpolated string TEXT
            my $verb = $stack[-1][0];
            if ($ch eq '{' && $nx eq '{') { $blank->(\@code, $i, $i + 1); $i += 2; next }
            if ($ch eq '}' && $nx eq '}') { $blank->(\@code, $i, $i + 1); $i += 2; next }
            if ($ch eq '{') { $stack[-1][1] = 1; $i++; next }
            if ($ch eq '"') {
                if ($verb && $nx eq '"') { $blank->(\@code, $i, $i + 1); $i += 2; next }
                pop @stack; $i++; next;                            # the closing quote stays in both views
            }
            if (!$verb && $ch eq '\\') { $blank->(\@code, $i, $i + 1); $i += 2; next }
            $code[$i] = ' ' if $ch ne "\n";
            $i++; next;
        }
        if ($ch eq '/' && $nx eq '/') { my $e = index($s, "\n", $i); $e = $n if $e < 0; $blank->(\@code, $i, $e - 1); $blank->(\@text, $i, $e - 1); $i = $e; next }
        if ($ch eq '/' && $nx eq '*') { my $e = index($s, '*/', $i + 2); $e = $e < 0 ? $n : $e + 2; $blank->(\@code, $i, $e - 1); $blank->(\@text, $i, $e - 1); $i = $e; next }
        if ($ch eq "'") {
            my $j = $i + 1;
            while ($j < $n && $code[$j] ne "'" && $code[$j] ne "\n") { $j++ if $code[$j] eq '\\'; $j++ }
            $blank->(\@code, $i + 1, $j - 1); $i = $j + 1; next;
        }
        if (($ch eq '$' && $nx eq '"') || (($ch eq '$' || $ch eq '@') && ($nx eq '@' || $nx eq '$') && $i + 2 < $n && $code[$i + 2] eq '"')) {
            my $len = $nx eq '"' ? 2 : 3;
            push @stack, [$len == 3 ? 1 : 0, 0];
            $i += $len; next;
        }
        if ($ch eq '@' && $nx eq '"') {                           # verbatim string: "" is the only escape
            my $j = $i + 2;
            while ($j < $n) { if ($code[$j] eq '"') { if ($j + 1 < $n && $code[$j + 1] eq '"') { $j += 2; next } last } $j++ }
            $blank->(\@code, $i + 2, $j - 1); $i = $j + 1; next;
        }
        if ($ch eq '"') {                                         # plain string
            my $j = $i + 1;
            while ($j < $n && $code[$j] ne '"' && $code[$j] ne "\n") { $j++ if $code[$j] eq '\\'; $j++ }
            $blank->(\@code, $i + 1, $j - 1); $i = $j + 1; next;
        }
        if (@stack) {
            if    ($ch eq '{') { $stack[-1][1]++ }
            elsif ($ch eq '}') { $stack[-1][1]-- }
        }
        $i++;
    }
    return (join('', @code), join('', @text));
}

# the arguments of a call whose opening paren is at $open (CODE view), split at depth 1; returns the TEXT of each
sub call_args {
    my ($code, $text, $open) = @_;
    my $depth = 1;
    my @spans;
    my $a = $open + 1;
    for (my $j = $open + 1; $j < length $code; $j++) {
        my $c = substr($code, $j, 1);
        if    ($c eq '(' || $c eq '[' || $c eq '{') { $depth++ }
        elsif ($c eq ')' || $c eq ']' || $c eq '}') { $depth--; if ($depth == 0) { push @spans, [$a, $j]; last } }
        elsif ($c eq ',' && $depth == 1) { push @spans, [$a, $j]; $a = $j + 1 }
    }
    return map { my $t = substr($text, $_->[0], $_->[1] - $_->[0]); $t =~ s/^\s+|\s+$//g; $t } @spans;
}

sub literals_in { my ($t) = @_; return ($t =~ /"([A-Za-z_][A-Za-z0-9_.]*)"/g) }
sub literal_list {
    my ($body) = @_;
    my $rest = $body;
    $rest =~ s/"[A-Za-z_][A-Za-z0-9_.]*"//g;
    return undef if $rest =~ /[^\s,]/;  # a computed element must not be hidden by neighbouring literals
    my @names = literals_in($body);
    return @names ? \@names : undef;
}

my %files;        # file -> { code, text }
my %tableDefs;    # identifier -> [{file, names}]; a table in another partial is usable only when unambiguous
sub add_table {
    my ($f, $name, $names) = @_;
    push @{ $tableDefs{$name} }, { file => $f, names => $names };
}
sub table_at {
    my ($f, $name) = @_;
    my @defs = @{ $tableDefs{$name} // [] };
    my @local = grep { $_->{file} eq $f } @defs;
    @defs = @local if @local;
    return undef unless @defs;
    my $sig = join "\0", @{ $defs[0]{names} // [] };
    return undef if grep { !defined($_->{names}) || join("\0", @{ $_->{names} }) ne $sig } @defs;
    return $defs[0]{names};
}
for my $f (@ARGV) {
    open(my $fh, '<', $f) or next;
    my $src = do { local $/; <$fh> };
    close $fh;
    $src =~ s/\r//g;
    my ($code, $text) = views($src);
    $files{$f} = { code => $code, text => $text };
    while ($text =~ /\bstring\s*\[\]\s+(\w+)\s*=\s*(?:new\s*(?:string)?\s*\[\]\s*)?\{([^{}]*)\}/g) {
        my ($name, $body) = ($1, $2);
        add_table($f, $name, literal_list($body));
    }
    while ($text =~ /\b(?:const\s+string|static\s+readonly\s+string|string)\s+(\w+)\s*=\s*("[A-Za-z_][A-Za-z0-9_.]*")\s*;/g) {
        add_table($f, $1, [literals_in($2)]);
    }
}

# every function header in CODE: name, params, body span (brace-matched, or `=> …;`)
sub headers {
    my ($code) = @_;
    my @h;
    while ($code =~ /\b([A-Za-z_]\w*)(?:<[^<>()]*>)?\s*\(([^()]*)\)\s*(=>|\{)/g) {
        my ($name, $params, $kind) = ($1, $2, $3);
        my $start = $-[0];
        next if $name =~ /^(?:if|for|foreach|while|switch|catch|lock|using|return|new|else)$/;
        my ($bodyStart, $bodyEnd) = ($-[3], undef);
        if ($kind eq '=>') { $bodyEnd = index($code, ';', $bodyStart); $bodyEnd = length($code) if $bodyEnd < 0 }
        else {
            my $depth = 0;
            for (my $j = $bodyStart; $j < length $code; $j++) {
                my $c = substr($code, $j, 1);
                if ($c eq '{') { $depth++ } elsif ($c eq '}') { $depth--; if ($depth == 0) { $bodyEnd = $j; last } }
            }
            $bodyEnd //= length $code;
        }
        push @h, { name => $name, params => $params, start => $start, end => $bodyEnd };
    }
    return @h;
}

my %headersOf;
sub enclosing {   # innermost function whose body holds $pos and whose params declare `string $ident`
    my ($f, $pos, $ident) = @_;
    $headersOf{$f} //= [headers($files{$f}{code})];
    my $best;
    for my $h (@{ $headersOf{$f} }) {
        next unless $h->{start} < $pos && $pos < $h->{end};
        next unless $h->{params} =~ /\bstring\s+\Q$ident\E\b/;
        $best = $h if !$best || $h->{start} > $best->{start};
    }
    return $best;
}

sub param_index {   # position of `string $ident` among the parameters
    my ($params, $ident) = @_;
    my @p = split /,/, $params;
    for my $i (0 .. $#p) { return $i if $p[$i] =~ /\bstring\s+\Q$ident\E\b/ }
    return -1;
}

sub closing_delimiter {
    my ($code, $open, $left, $right) = @_;
    my $depth = 0;
    for (my $j = $open; $j < length $code; $j++) {
        my $c = substr($code, $j, 1);
        $depth++ if $c eq $left;
        if ($c eq $right) { $depth--; return $j if $depth == 0 }
    }
    return -1;
}

sub statement_end {
    my ($code, $body) = @_;
    $body++ while $body < length($code) && substr($code, $body, 1) =~ /\s/;
    return closing_delimiter($code, $body, '{', '}') if substr($code, $body, 1) eq '{';
    my ($parens, $brackets, $braces) = (0, 0, 0);
    for (my $j = $body; $j < length($code); $j++) {
        my $c = substr($code, $j, 1);
        $parens++ if $c eq '('; $parens-- if $c eq ')';
        $brackets++ if $c eq '['; $brackets-- if $c eq ']';
        if ($c eq '{' && !$parens && !$brackets && !$braces) {
            return closing_delimiter($code, $j, '{', '}');  # `if (...) { ... }` as the single statement
        }
        $braces++ if $c eq '{'; $braces-- if $c eq '}';
        return $j if $c eq ';' && !$parens && !$brackets && !$braces;
    }
    return -1;
}

# A preceding foreach only owns its variable INSIDE its body. A same-named parameter in a later method must not
# inherit its literals (otherwise an untraceable reader silently passes the catalog gate).
sub loop_source {
    my ($code, $text, $pos, $ident) = @_;
    my $source;
    while ($code =~ /\bforeach\s*\(\s*(?:var|string)\s+\Q$ident\E\s+in\s*/g) {
        my $srcStart = pos($code);
        my $open = index($code, '(', $-[0]);
        my $close = closing_delimiter($code, $open, '(', ')');
        next if $close < 0 || $close >= $pos;
        my $end = statement_end($code, $close + 1);
        next if $end < $pos;
        $source = substr($text, $srcStart, $close - $srcStart);
    }
    return $source;
}

# A small literal-index loop spells a finite member set (`"Pose" + i + ".Time"`). The catalog must bind every
# generated name, not merely its endpoints. Only accept a loop whose body contains this reader.
sub loop_bounds {
    my ($code, $pos, $ident) = @_;
    my $bounds;
    while ($code =~ /\bfor\s*\(\s*int\s+\Q$ident\E\s*=\s*(\d+)\s*;\s*\Q$ident\E\s*<\s*(\d+)\s*;\s*\Q$ident\E\s*\+\+\s*\)/g) {
        my ($first, $limit) = ($1, $2);
        my $close = pos($code) - 1;
        next if $close >= $pos || statement_end($code, $close + 1) < $pos;
        next if $limit < $first || $limit - $first > 64;
        $bounds = [$first, $limit];
    }
    return $bounds;
}

# resolve one argument text at a site; returns (\@literals) or undef when it cannot
sub resolve {
    my ($f, $pos, $arg, $depth) = @_;
    return undef if $depth > 3;
    my ($code, $text) = ($files{$f}{code}, $files{$f}{text});
    return [literals_in($arg)] if $arg =~ /^"[A-Za-z_][A-Za-z0-9_.]*"$/;
    if ($arg =~ /^"([A-Za-z_][A-Za-z0-9_]*)"\s*\+\s*([A-Za-z_]\w*)(?:\s*\+\s*"(\.[A-Za-z_][A-Za-z0-9_]*)")?$/) {
        my ($prefix, $index, $suffix) = ($1, $2, $3 // '');
        my $bounds = loop_bounds($code, $pos, $index);
        return [map { "$prefix$_$suffix" } $bounds->[0] .. $bounds->[1] - 1] if $bounds;
        return undef;
    }
    my $ident = $arg =~ /^([A-Za-z_]\w*)\s*\[[^\]]*\]$/ ? $1 : $arg =~ /^([A-Za-z_]\w*)$/ ? $1 : undef;
    return undef unless defined $ident;
    # 1. a loop variable: the nearest preceding foreach that declares it
    my $before = substr($code, 0, $pos);
    if (defined(my $srcText = loop_source($code, $text, $pos, $ident))) {
        if ($srcText =~ /^new\s*(?:string)?\s*\[\]\s*\{([^{}]*)\}/) { return literal_list($1) }
        if ($srcText =~ /^([A-Za-z_]\w*)\s*$/) { return table_at($f, $1) }
        return undef;
    }
    # 2. a parameter shadows a field/table of the same name. Resolve its callers before consulting file-wide tables.
    my $h = enclosing($f, $pos, $ident);
    if ($h) {
        return [] if $known{ $h->{name} };  # known helper: its callers' literals are extracted by the gate
        my $idx = param_index($h->{params}, $ident);
        my @out;
        my $found = 0;
        while ($code =~ /(?<![\w.])\Q$h->{name}\E\s*\(/g) {
            my $open = $-[0] + length($&) - 1;
            next if $open >= $h->{start} && $open < $h->{end} && $open < $h->{start} + length($h->{name}) + 2;   # the header itself
            my @args = call_args($code, $text, $open);
            next if $idx < 0 || $idx > $#args;
            $found++;
            my $r = resolve($f, $open, $args[$idx], $depth + 1);
            return undef unless $r;
            push @out, @$r;
        }
        return $found ? \@out : undef;
    }
    # 3. a table or a local constant
    my $table = table_at($f, $ident);
    return $table if $table;
    if ($before =~ /.*\b(?:var|string)\s+\Q$ident\E\s*=\s*/s) {
        my $init = substr($text, $+[0]);
        if ($init =~ /^[^;]*?\?\s*"([A-Za-z_][A-Za-z0-9_.]*)"\s*:\s*"([A-Za-z_][A-Za-z0-9_.]*)"\s*;/) { return [$1, $2] }
        if ($init =~ /^"([A-Za-z_][A-Za-z0-9_.]*)"\s*;/) { return [$1] }
    }
    return undef;
}

for my $f (sort keys %files) {
    my ($code, $text) = ($files{$f}{code}, $files{$f}{text});
    # a HAF-qualified helper call (`UniversalInject.GetMember(`) must read as the helper, not as Type.GetMember:
    # blank the qualifier so the helper alternative is the leftmost match
    (my $scan = $code) =~ s/\b[A-Za-z_]\w*\.(?=(?:$helperAlt)\s*\()/' ' x length($&)/ge;
    $headersOf{$f} //= [headers($code)];
    my %headerParen = map { (index($code, '(', $_->{start} + length($_->{name})) => 1) } @{ $headersOf{$f} };
    while ($scan =~ /$accessor/g) {
        my $kind = defined $+{h} ? 'helper' : defined $+{gx} ? 'getx' : defined $+{tr} ? 'traverse' : defined $+{at} ? 'accesstools' : 'fastmember';
        my $open = pos($scan) - 1;
        next if $headerParen{$open};                          # a helper's own declaration, not a call
        my $line = 1 + (substr($code, 0, $open) =~ tr/\n//);
        my @args = call_args($code, $text, $open);
        my $idx = ($kind eq 'getx' || $kind eq 'traverse') ? 0 : 1;
        my $a = $args[$idx];
        next unless defined $a && length $a;
        next if $a =~ /^"[A-Za-z_][A-Za-z0-9_.]*"$/;          # a whole literal: the first two passes own it
        my $r = resolve($f, $open, $a, 0);
        if (!$r) {
            my $h = enclosing($f, $open, $a =~ /^([A-Za-z_]\w*)/ ? $1 : '');
            my $ctx = $h ? "$h->{name}(" . ($h->{params} =~ s/\s+/ /gr) . ")" : '?';
            print "UNRESOLVED\t$a\t$f:$line\t$ctx\n";
            next;
        }
        print "$_\t$f:$line\n" for @$r;
    }
}
