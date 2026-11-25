using OrbSolver.Contracts;

var cells = new OrbType[30];
Array.Fill(cells, OrbType.Water);
var snapshot = new BoardSnapshot(cells);
snapshot.Cells[0] = OrbType.Fire;
