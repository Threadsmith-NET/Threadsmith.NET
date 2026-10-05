namespace Threadsmith.Execution;

/// <summary>Shared operation schema for mutation instruction producers.</summary>
internal static class MutationInstructionSchema
{
    /// <summary>Source-edit arguments containing the shared operation definitions.</summary>
    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": [
            "mutations",
            "rationale"
          ],
          "properties": {
            "mutations": {
              "type": "array",
              "minItems": 0,
              "maxItems": 100,
              "items": {
                "oneOf": [
                  {
                    "$ref": "#/$defs/createFile"
                  },
                  {
                    "$ref": "#/$defs/deleteFile"
                  },
                  {
                    "$ref": "#/$defs/replaceText"
                  },
                  {
                    "$ref": "#/$defs/renameSymbol"
                  },
                  {
                    "$ref": "#/$defs/moveFile"
                  }
                ]
              }
            },
            "rationale": {
              "type": "string"
            }
          },
          "$defs": {
            "content": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "text"
              ],
              "properties": {
                "text": {
                  "type": "string"
                },
                "encoding": {
                  "type": "string",
                  "enum": [
                    "Utf8",
                    "Utf8Bom"
                  ]
                },
                "newline": {
                  "type": "string",
                  "enum": [
                    "Lf",
                    "CrLf"
                  ]
                }
              }
            },
            "createFile": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "type",
                "relativePath",
                "content"
              ],
              "properties": {
                "type": {
                  "type": "string",
                  "const": "CreateFile"
                },
                "relativePath": {
                  "type": "string"
                },
                "content": {
                  "$ref": "#/$defs/content"
                },
                "projectFilePath": {
                  "type": "string"
                }
              }
            },
            "deleteFile": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "type",
                "relativePath"
              ],
              "properties": {
                "type": {
                  "type": "string",
                  "const": "DeleteFile"
                },
                "relativePath": {
                  "type": "string"
                },
                "projectFilePath": {
                  "type": "string"
                }
              }
            },
            "replaceText": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "type",
                "relativePath",
                "expectedText",
                "replacementText"
              ],
              "properties": {
                "type": {
                  "type": "string",
                  "const": "ReplaceText"
                },
                "relativePath": {
                  "type": "string"
                },
                "startOffset": {
                  "type": [
                    "integer",
                    "null"
                  ],
                  "minimum": 0
                },
                "expectedText": {
                  "type": "string"
                },
                "replacementText": {
                  "type": "string"
                },
                "relatedSymbolId": {
                  "type": "string"
                },
                "projectFilePath": {
                  "type": "string"
                }
              }
            },
            "renameSymbol": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "type",
                "relativePath",
                "relatedSymbolId",
                "replacementText"
              ],
              "properties": {
                "type": {
                  "type": "string",
                  "const": "RenameSymbol"
                },
                "relativePath": {
                  "type": "string"
                },
                "relatedSymbolId": {
                  "type": "string"
                },
                "replacementText": {
                  "type": "string"
                },
                "projectFilePath": {
                  "type": "string"
                }
              }
            },
            "moveFile": {
              "type": "object",
              "additionalProperties": false,
              "required": [
                "type",
                "relativePath",
                "destinationRelativePath"
              ],
              "properties": {
                "type": {
                  "type": "string",
                  "const": "MoveFile"
                },
                "relativePath": {
                  "type": "string"
                },
                "destinationRelativePath": {
                  "type": "string"
                },
                "content": {
                  "$ref": "#/$defs/content"
                },
                "projectFilePath": {
                  "type": "string"
                }
              }
            }
          }
        }
        """;
}
