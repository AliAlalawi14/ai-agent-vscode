import sys
import json
import chromadb

client = chromadb.PersistentClient(path="./chroma_data")

action = sys.argv[1]

if action == "init":
    try:
        client.create_collection(name="code_chunks")
        print(json.dumps({"status": "created"}))
    except:
        print(json.dumps({"status": "exists"}))

elif action == "add":
    data = json.loads(sys.argv[2])
    collection = client.get_collection(name="code_chunks")
    collection.add(
        ids=data["ids"],
        embeddings=data["embeddings"],
        documents=data["documents"],
        metadatas=data["metadatas"]
    )
    print(json.dumps({"status": "added", "count": len(data["ids"])}))

elif action == "query":
    data = json.loads(sys.argv[2])
    collection = client.get_collection(name="code_chunks")
    results = collection.query(
        query_embeddings=data["query_embeddings"],
        n_results=data.get("n_results", 5)
    )
    # Convert to serializable format
    output = {
        "ids": results.get("ids", [[]])[0] if results.get("ids") else [],
        "documents": results.get("documents", [[]])[0] if results.get("documents") else [],
        "distances": results.get("distances", [[]])[0] if results.get("distances") else [],
        "metadatas": results.get("metadatas", [[]])[0] if results.get("metadatas") else []
    }
    print(json.dumps(output))