/// <reference types="tree-sitter-cli/dsl" />

const SYMBOL_START = /[a-zA-Z_+\-*/=<>!?&|%^~#@]/;
const SYMBOL_CONTINUE = /[a-zA-Z_+\-*/=<>!?&|%^~#@0-9.\->]/;

module.exports = grammar({
  name: "zscheme",

  extras: ($) => [/\s/, $.comment],

  rules: {
    source_file: ($) => repeat($._form),

    _form: ($) =>
      choice(
        $.list,
        $.bracket_list,
        $.string,
        $.float,
        $.number,
        $.boolean,
        $.clr_qualifier,
        $.type_variable,
        $.wildcard,
        $.ellipsis,
        $.symbol,
        $.quote,
        $.quasiquote,
        $.unquote_splicing,
        $.unquote,
        $.colon,
      ),

    comment: ($) => /;.*/,

    // S-expression: (head form*)
    list: ($) =>
      choice(
        seq("(", ")"),
        seq("(", field("head", $._form), repeat($._form), ")"),
      ),

    // Bracket form: [x : Int]
    bracket_list: ($) => seq("[", repeat($._form), "]"),

    // Literals
    number: ($) => token(seq(optional("-"), /[0-9]+/)),

    float: ($) =>
      token(
        choice(
          seq(optional("-"), /[0-9]+/, ".", /[0-9]+/, optional(/[fF]/)),
          seq(optional("-"), /[0-9]+/, /[fF]/),
          seq(".", /[0-9]+/),
        ),
      ),

    string: ($) =>
      seq('"', repeat(choice($.escape_sequence, /[^"\\]+/)), '"'),

    escape_sequence: ($) => token.immediate(/\\[ntr\\"']/),

    boolean: ($) => token(choice("#t", "#f")),

    // CLR qualifiers: :instance, :instance-property, :instance-indexer, :where
    clr_qualifier: ($) =>
      token(
        seq(
          ":",
          choice("instance-property", "instance-indexer", "instance", "where"),
        ),
      ),

    // Type variable: ^a, ^b
    type_variable: ($) =>
      token(seq("^", /[a-z]/, repeat(SYMBOL_CONTINUE))),

    // Symbol: identifiers, keywords, operators — all classified in highlights.scm
    symbol: ($) => token(seq(SYMBOL_START, repeat(SYMBOL_CONTINUE))),

    // Special tokens
    wildcard: ($) => "_",
    ellipsis: ($) => "...",
    colon: ($) => ":",

    // Reader macros
    quote: ($) => seq("'", $._form),
    quasiquote: ($) => seq("`", $._form),
    unquote_splicing: ($) => seq(",@", $._form),
    unquote: ($) => seq(",", $._form),
  },

  conflicts: ($) => [],

  precedences: ($) => [
    [
      "float",
      "number",
    ],
  ],

  word: ($) => $.symbol,
});
