# Lessons Learned

## Paradise ECS Generators

- [hits: 1] Never interpolate generic type names such as `Data<TMask, TConfig>` directly into generated XML doc comments; unescaped `<` causes CS1570. Describe the API in plain text or XML-escape the type name.
